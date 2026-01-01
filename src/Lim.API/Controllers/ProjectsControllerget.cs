#nullable disable

using System.Collections.Immutable;
using System.Web;
using JetBrains.Annotations;
using Lim.API.Contracts;
using Lim.Common.DotNET;
using Lim.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace Lim.API;

[Route("api/projects")]
public class ProjectsController(
    IProjectsStorage projectsStorage,
    IProjectProfilesStorage projectProfilesStorage,
    IDeveloperProjectProfilesStorage developerProjectProfilesStorage,
    IRepositoryProjectProfileStorage repositoryProjectProfileStorage,
    IDeveloperProfilesStorage developerProfilesStorage,
    IRepositoryProfilesStorage repositoryProfilesStorage,
    IAuditActionsService auditActionsService,
    ICustomFiltersStorage customFiltersStorage,
    IDeveloperIdentityStorage developerIdentityStorage,
    IGovernanceRulesStorage governanceRulesStorage,
    IMessageQueuePublisher messageQueuePublisher,
    IIssuesStorage issuesStorage,
    ITimelineEventsStorage timelineEventsStorage,
    IRepositoriesStorage repositoriesStorage,
    IAssetCollectionProfilesStorage assetCollectionProfilesStorage,
    IProjectsProvider projectsProvider,
    IProfileRiskTrendStatisticsStorage riskTrendStatisticsStorage,
    IDeployKeysStorage deployKeysStorage,
    IProfileLearningStatisticsStorage profileLearningStatisticsStorage,
    ILearningStatisticsEnricher learningStatisticsEnricher,
    IConsumablesFilterOptions consumablesFilterOptions,
    IPullRequestsStorage pullRequestsStorage,
    IPullRequestIssuesResolver pullRequestIssuesResolver,
    IApiSecurityHintService apiSecurityHintService,
    IProviderRepositoriesStorage providerRepositoriesStorage,
    IServersStorage serversStorage,
    IIssueFieldsMatchingService issueFieldsMatchingService,
    ITableFiltersService tableFiltersService,
    IProjectIssueTypesService projectIssueTypesService,
    IExporterService exporterService,
    IInventoryElementStorage inventoryElementStorage
) : ConsumableEntityControllerBase(
    AuditEntity.Project,
    auditActionsService,
    customFiltersStorage,
    governanceRulesStorage,
    repositoryProfilesStorage,
    projectProfilesStorage,
    assetCollectionProfilesStorage,
    riskTrendStatisticsStorage,
    deployKeysStorage,
    developerProfilesStorage,
    profileLearningStatisticsStorage,
    learningStatisticsEnricher,
    consumablesFilterOptions,
    issuesStorage,
    apiSecurityHintService,
    messageQueuePublisher,
    providerRepositoriesStorage,
    tableFiltersService,
    exporterService,
    inventoryElementStorage
)
{
    private readonly IAssetCollectionProfilesStorage _assetCollectionProfilesStorage = assetCollectionProfilesStorage;
    private readonly IProjectProfilesStorage _projectProfilesStorage = projectProfilesStorage;
    private readonly IRepositoryProfilesStorage _repositoryProfilesStorage = repositoryProfilesStorage;

    protected override string ProfileType => nameof(ProjectProfile);
    protected override string SpecificProfileType => nameof(ProjectProfile);

    protected override TableConsumable TableConsumable => TableConsumable.ProjectProfile;

    protected override Task EnrichLearningStatisticsAsync(ProfileLearningStatistics profileLearningStatistics, string key)
        => Task.CompletedTask;

    [HttpGet("v2/search/lean")]
    public async Task<AggregationResult<LeanConsumable>> SearchLeanProjectsAsync(
        [FromQuery] int skip,
        [FromQuery] [CanBeNull] string searchTerm,
        [FromQuery] int pageSize = 20
    )
    {
        var result = await SearchProjectsAsync(
            skip,
            searchTerm,
            tableFilterToQuery: null,
            pageSize
        );

        return new AggregationResult<LeanConsumable>(result.Items.Select(LeanConsumable.CreateFromConsumable).ToList(), result.Count, result.Total);
    }

    [HttpGet("v2/{provider}/search")]
    public async Task<AggregationResult<Project>> SearchMonitoredProjectsAsync(
        ProviderGroup provider, [FromQuery] int skip,
        [FromQuery] [CanBeNull] string searchTerm,
        [FromQuery] int pageSize = 20
    )
        => await projectsStorage.SearchProjectsByProviderAsync(
            provider,
            skip,
            pageSize,
            TableFiltersService.GetSearchFilters(Project.SearchFields, searchTerm)
        );

    [HttpGet("v2/{provider}")]
    [AccessTokenPermissions(RoleResource.Global)]
    public async Task<IReadOnlyCollection<Project>> GetMonitoredProjectsAsync(ProviderGroup provider)
        => (await projectsStorage.GetMonitoredProjectsAsync(provider)).Where(_ => _.IsRelevant).ToImmutableList();

    [HttpGet("v2/{provider}/{key}/issueTypes/options")]
    public async Task<IReadOnlyCollection<IssueTypeOption>> GetProjectIssueTypeOptionsAsync(ProviderGroup provider, string key)
    {
        var project = await projectsStorage.GetAsync(key);
        var issueTypes = await projectIssueTypesService.GetProjectIssueTypesAsync(project);
        return issueTypes
            .Select(
                _ =>
                {
                    return provider switch
                    {
                        ProviderGroup.Jira => new JiraIssueTypeOption(_, new List<IssueTypeField>()) as IssueTypeOption,
                        ProviderGroup.AzureDevops => new AzureDevopsIssueTypeOption(_, new List<IssueTypeField>()),
                        _ => throw new ArgumentException($"{provider} not supported")
                    };
                }
            )
            .ToList();
    }

    [HttpGet("{key}")]
    public async Task<Project> GetProjectAsync(string key)
        => await projectsStorage.GetAsync(key);

    [HttpPut]
    [AuthorizeResource(RoleResource.Connectors)]
    public async Task<IActionResult> UpdateProjectsAsync([FromBody] UpdateManyMonitorBin updateManyMonitorBin)
    {
        var keys = await projectsStorage.SyncMonitorStatusesForKeysAsync(
            updateManyMonitorBin.Keys,
            updateManyMonitorBin.MonitorStatus
        );

        var projectsByKey = (await projectsStorage.GetAllAsync(keys)).ToDictionary(_ => _.Key);

        if (updateManyMonitorBin.MonitorStatus == MonitorStatus.Monitored)
        {
            var projectProfiles = projectsByKey
                .Values
                .Select(ProjectProfile.EmptyProfileFor)
                .ToList();
            await Task.WhenAll(
                _projectProfilesStorage.UpsertManyAsync(projectProfiles),
                projectProfiles.ParallelForEachAwaitAsync(
                    async projectProfile => await MessageQueuePublisher.PublishAsync(
                        new ProjectMonitoredChangedMessage(projectProfile.Key, updateManyMonitorBin.MonitorStatus),
                        QueueNames.ProjectMonitoredChanged
                    )
                )
            );
        }

        await Task.WhenAll(
            CleanupConsumableEntitiesByKeysIfNeededAsync<Project>(updateManyMonitorBin, keys),
            keys.ParallelForEachAwaitAsync(
                async key =>
                {
                    var projectExists = projectsByKey.TryGetValue(key, out var project);
                    await AuditActionsService.AuditMonitorConsumableAsync(
                        typeof(Project),
                        key,
                        projectExists
                            ? project.ToAuditLogDescription()
                            : key,
                        updateManyMonitorBin.MonitorStatus,
                        projectExists
                            ? project.Server
                            : null
                    );
                }
            )
        );

        return Accepted(
            updateManyMonitorBin
                .Keys.Except(keys)
                .ToList()
        );
    }

    [HttpPut("{key}")]
    [AuthorizeResource(RoleResource.Connectors)]
    public async Task<IActionResult> UpdateProjectAsync(string key, [FromBody] UpdateMonitorBin updateMonitorBin)
    {
        Project project;
        try
        {
            await projectsStorage.SetMonitoredAsync(
                key,
                updateMonitorBin.MonitorStatus
            );

            project = await projectsStorage.GetAsync(key);
            await MessageQueuePublisher.PublishAsync(
                new ProjectMonitoredChangedMessage(key, updateMonitorBin.MonitorStatus),
                QueueNames.ProjectMonitoredChanged
            );

            if (updateMonitorBin.MonitorStatus == MonitorStatus.Monitored)
            {
                await _projectProfilesStorage.UpsertManyAsync(new[] { ProjectProfile.EmptyProfileFor(project) });
            }
            else
            {
                await MessageQueuePublisher.PublishAsync(
                    EntityCleanupMessage.Create<Project>(key),
                    QueueNames.CleanupRemovedEntity
                );
            }
        }
        catch (EntityNotPersistedException)
        {
            return NotFound($"No project with key '{key}'");
        }

        await AuditActionsService.AuditMonitorConsumableAsync(
            typeof(Project),
            key,
            project?.ToAuditLogDescription(),
            updateMonitorBin.MonitorStatus,
            project?.Server
        );

        return Accepted(key);
    }

    [HttpPut("scanIssues")]
    public async Task<IActionResult> ScanProjectsIssuesAsync([FromBody] HashSet<string> projectKeys)
    {
        var projects = await projectsStorage.GetProjectsAsync(projectKeys);

        if (projectKeys.Count != projects.Count ||
            projects.Any(project => !project.IsMonitored))
        {
            return BadRequest("Not all given project keys are valid");
        }

        await MessageQueuePublisher.PublishAsync(projectKeys, QueueNames.TriggerProjectIssuesSync);

        return Ok();
    }

    [HttpGet("search")]
    public async Task<AggregationResult<Project>> GetProjectsAsync(
        [FromQuery] int skip,
        [FromQuery] [CanBeNull] string searchTerm,
        [FromQuery(Name = "tableFilterToQuery")] [CanBeNull]
        Dictionary<string, List<string>> tableFilterToQuery,
        [FromQuery] int pageSize = 20
    )
        => await SearchProjectsAsync(
            skip,
            searchTerm,
            tableFilterToQuery,
            pageSize
        );

    [HttpGet("filterOptions")]
    public async Task<List<ConsumablesFilterOptionsGroup>> GetProjectFilterOptionsAsync([FromQuery] bool isSingleConnection)
        => await ConsumablesFilterOptions.FilterOptionsByGroupAndSingleConnectionAsync(isSingleConnection, TableConsumable.Project);

    [HttpGet("profiles/search")]
    public async Task<AggregationResult<ProjectProfile>> GetProjectProfilesAsync(
        [FromQuery] int skip,
        [FromQuery] int pageSize,
        [FromQuery] [CanBeNull] string searchTerm,
        [FromQuery(Name = "tableFilterToQuery")] [CanBeNull]
        Dictionary<string, List<string>> tableFilterToQuery,
        [FromQuery] [CanBeNull] string sortOption
    )
    {
        var result = await _projectProfilesStorage.GetProfilesAndCountAsync(
            skip,
            pageSize,
            TableFiltersService.GetSearchFilters(ProjectProfile.SearchFields, searchTerm),
            await TableFiltersService.GetFiltersAsync(tableFilterToQuery),
            TableSortParsingExtensions.ToTableSortOption(sortOption),
            TableFilterOperator.And
        );

        var servers = await serversStorage.GetServersAsync();
        var serverByUrl = servers.ToDictionary(_ => _.Url);
        result
            .Items.SelectNotNull(_ => _.Project)
            .EnrichServer(serverByUrl);

        return result;
    }

    [HttpGet("{key}/profile")]
    public async Task<ProjectProfile> GetProjectProfileAsync(string key)
        => await _projectProfilesStorage.GetProjectProfileByKeyAsync(key);

    [HttpGet("{key}/timelineEvents")]
    public async Task<EnrichedTimelineEvents> GetTimelineEventsAsync(
        string key,
        [FromQuery] int skip,
        [FromQuery] int pageSize,
        [FromQuery(Name = "tableFilterToQuery")] [CanBeNull]
        Dictionary<string, List<string>> tableFilterToQuery
    )
    {
        var project = await projectsStorage.GetAsync(key);
        var timelineEvents = await timelineEventsStorage.GetProjectTimelineEventsAsync(
            project,
            skip,
            pageSize,
            await TableFiltersService.GetFiltersAsync(tableFilterToQuery)
        );
        return await EnrichedTimelineEvents.EnrichAndFilterAsync(
            timelineEvents,
            DeveloperProfilesStorage,
            _repositoryProfilesStorage,
            repositoriesStorage,
            ProviderRepositoriesStorage,
            _projectProfilesStorage,
            projectsStorage,
            developerIdentityStorage,
            GovernanceRulesStorage,
            pullRequestsStorage,
            pullRequestIssuesResolver
        );
    }

    [HttpGet("labels")]
    public async Task<IReadOnlySet<string>> GetProjectsLabelsAsync([FromQuery] HashSet<string> projectIds, [FromQuery] string filter = null)
        => await IssuesStorage.GetProjectsLabelsAsync(projectIds, filter);

    [HttpGet("repositories/filterOptions")]
    public Task<List<ConsumablesFilterOptionsGroup>> GetProjectRepositoriesFilterOptionsAsync()
        => ConsumablesFilterOptions.FilterOptionsByGroupAsync(TableConsumable.ProjectRepositoryProfile);

    [HttpGet("{key}/repositories/search")]
    [OnExceptionFallbackToEmptyArrayFilter]
    public async Task<AggregationResult<RelatedEntityProfile<RepositoryProfile, RepositoryProjectProfile>>> GetRepositoriesAsync(
        string key,
        [FromQuery] int skip,
        [FromQuery] int pageSize,
        [FromQuery] [CanBeNull] string searchTerm,
        [FromQuery(Name = "tableFilterToQuery")] [CanBeNull]
        Dictionary<string, List<string>> tableFilterToQuery
    )
    {
        var repositoryProjectProfilesAndCount = await repositoryProjectProfileStorage.GetProfilesForProjectAndCountAsync(
            key,
            skip,
            pageSize,
            TableFiltersService.GetSearchFilters(RepositoryProjectProfile.RepositorySearchFields, searchTerm),
            await TableFiltersService.GetFiltersAsync(tableFilterToQuery)
        );
        return new AggregationResult<RelatedEntityProfile<RepositoryProfile, RepositoryProjectProfile>>(
            await RelatedEntityProfile<RepositoryProfile, RepositoryProjectProfile>.EnrichAsync(
                repositoryProjectProfilesAndCount.Items,
                repositoryProjectProfile => repositoryProjectProfile.RepositoryKey,
                async repositoryKeys => (
                    await _repositoryProfilesStorage.GetProfilesAsync(repositoryKeys)
                ).ToDictionary(profile => profile.Key)
            ),
            repositoryProjectProfilesAndCount.Count
        );
    }

    [HttpGet("developers/filterOptions")]
    public Task<List<ConsumablesFilterOptionsGroup>> GetDeveloperProjectProfileFilterOptionsAsync()
        => ConsumablesFilterOptions.FilterOptionsByGroupAsync(TableConsumable.DeveloperProjectProfile);

    [HttpGet("{key}/developers/search")]
    [OnExceptionFallbackToEmptyArrayFilter]
    public async Task<AggregationResult<RelatedEntityProfile<DeveloperProfile, DeveloperProjectProfile>>> GetDevelopersAsync(
        string key,
        [FromQuery] int skip,
        [FromQuery] int pageSize,
        [FromQuery] [CanBeNull] string searchTerm,
        [FromQuery(Name = "tableFilterToQuery")] [CanBeNull]
        Dictionary<string, List<string>> tableFilterToQuery
    )
    {
        var developerProjectProfilesAndCount = await developerProjectProfilesStorage.GetProfilesAndCountForConsumableAsync(
            key,
            skip,
            pageSize,
            TableFiltersService.GetSearchFilters(DeveloperProfile.SearchFields, searchTerm),
            await TableFiltersService.GetFiltersAsync(tableFilterToQuery)
        );
        return new AggregationResult<RelatedEntityProfile<DeveloperProfile, DeveloperProjectProfile>>(
            await RelatedEntityProfile<DeveloperProfile, DeveloperProjectProfile>.EnrichAsync(
                developerProjectProfilesAndCount.Items,
                repositoryProjectProfile => repositoryProjectProfile.DeveloperKey,
                async developerKeys => (
                    await DeveloperProfilesStorage.GetDeveloperProfilesAsync(developerKeys)
                ).ToDictionary(profile => profile.Key)
            ),
            developerProjectProfilesAndCount.Count,
            developerProjectProfilesAndCount.Total
        );
    }

    [HttpGet("{key}/timelineEvents/filterOptions")]
    public async Task<IReadOnlyDictionary<string, long>> GetTimelineEventsFilterOptionsAsync(string key)
        => await timelineEventsStorage.GetProjectTimelineEventsFilterOptionsAsync(key);

    [HttpGet("assetCollections/filterOptions")]
    public Task<List<ConsumablesFilterOptionsGroup>> GetProjectsAssetCollectionFilterOptionsAsync()
        => ConsumablesFilterOptions.FilterOptionsByGroupAsync(TableConsumable.ApplicationProfile);

    [HttpGet("{key}/assetCollections/search")]
    [OnExceptionFallbackToEmptyArrayFilter]
    public async Task<AggregationResult<AssetCollectionProfile>> GetProjectsAssetCollectionsAsync(
        string key,
        [FromQuery] int skip,
        [FromQuery] int pageSize,
        [FromQuery] [CanBeNull] string searchTerm,
        [FromQuery(Name = "tableFilterToQuery")] [CanBeNull]
        Dictionary<string, List<string>> tableFilterToQuery
    )
    {
        var projectProfile = await _projectProfilesStorage.GetProfileByKeyAsync(key);
        return await _assetCollectionProfilesStorage.GetAssetCollectionProfilesAndCountAsync<ApplicationProfile>(
            projectProfile.AssetCollectionKeys,
            skip,
            pageSize,
            TableFiltersService.GetSearchFilters(AssetCollectionProfile.SearchFields, searchTerm),
            await TableFiltersService.GetFiltersAsync(tableFilterToQuery),
            enrichRelatedProfiles: true
        );
    }

    [HttpGet("{key}/users/search")]
    public async Task<IReadOnlyCollection<ProjectUser>> SearchProjectUsersAsync(string key, [FromQuery] string searchTerm)
    {
        var project = await projectsStorage.GetAsync(key);
        if (project == null)
        {
            return null;
        }

        return project.Server.ProviderGroup switch
        {
            ProviderGroup.Github => (await developerIdentityStorage.SearchDeveloperIdentityByServerAndTypeAsync(
                    searchTerm,
                    project.ServerUrl,
                    DeveloperIdentityType.GithubAccountLogin
                ))
                .Select(
                    _ => new ProjectUser
                    {
                        Key = _.Identity
                    }
                )
                .ToList(),
            _ => await projectsProvider.SearchAssignableUserAsync(project, searchTerm)
        };
    }

    [HttpGet("preFilledFields")]
    public async Task<List<FieldsDefault>> GetPreFilledFieldsAsync([FromQuery] string applicationKey, [FromQuery] string riskLevel, [FromQuery] string project, [FromQuery] string issueType)
        => await issueFieldsMatchingService.GetPreFilledFieldsAsync(
            applicationKey,
            riskLevel,
            project,
            issueType
        );

    [HttpGet("preFilledProjectIssueType")]
    public async Task<List<FieldsDefault>> GetPreFilledIssueTypeAsync([FromQuery] string applicationKey)
        => await issueFieldsMatchingService.GetPreFilledIssueTypeAndProjectAsync(applicationKey);

    [HttpGet("issueTypeOptions")]
    public async Task<IssueTypeOption> GetOptionForGivenIssueTypeAsync([FromQuery] ProviderGroup provider, [FromQuery] string projectKey, [FromQuery] string issueTypeId)
        => await issueFieldsMatchingService.GetOptionForGivenIssueTypeAsync(
            provider,
            projectKey,
            issueTypeId
        );

    [HttpGet("{key}/issues/search")]
    public async Task<IReadOnlyCollection<IssueSummary>> SearchIssuesAsync(
        string key, [FromQuery] string searchTerm,
        [FromQuery] bool openIssueOnly = true,
        [FromQuery] bool includeSubTask = false,
        [FromQuery] List<string> specificIssueTypes = null,
        [FromQuery] bool isParentSearch = false,
        [FromQuery] string issueType = null
    )
    {
        var project = await projectsStorage.GetAsync(key);
        if (project == null)
        {
            return null;
        }

        return await projectsProvider.GetIssuesSummaryByQueryAsync(
            project,
            searchTerm,
            openIssueOnly,
            includeSubTask,
            isParentSearch,
            issueType,
            specificIssueTypes
        );
    }

    [HttpGet("{projectKey}/labels")]
    public async Task<ActionResult<List<string>>> GetLabelsAsync(string projectKey)
    {
        if (string.IsNullOrEmpty(projectKey))
        {
            return new List<string>();
        }

        projectKey = HttpUtility.UrlDecode(projectKey);
        var project = await projectsStorage.GetAsync(projectKey);
        if (project == null)
        {
            return NotFound();
        }

        var labels = await projectIssueTypesService.GetIssueLabelsAsync(project);

        return Ok(
            labels
                .OrderBy(label => label.ToLower())
                .ToList()
        );
    }

    private async Task<AggregationResult<Project>> SearchProjectsAsync(int skip, string searchTerm, Dictionary<string, List<string>> tableFilterToQuery, int pageSize)
    {
        var result = await projectsStorage.GetPageWithCountAsync(
            skip,
            pageSize,
            TableFiltersService.GetSearchFilters(Project.SearchFields, searchTerm),
            await TableFiltersService.GetFiltersAsync(tableFilterToQuery)
        );

        var servers = await serversStorage.GetServersAsync();
        var serverByUrl = servers.ToDictionary(_ => _.Url);
        result.Items.EnrichServer(serverByUrl);

        return result;
    }
}
