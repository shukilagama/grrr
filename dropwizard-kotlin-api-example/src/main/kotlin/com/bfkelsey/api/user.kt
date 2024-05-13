import org.springframework.data.mongodb.core.mapping.Document

@Document
data class User (
        @Id
        val id: ObjectId = ObjectId.get(),
        val accountNumber: String,
        val createdAt: String,
        val document: String,
        val createdAt: String,
        val fullName: String,
        val createdAt: String,
        val lastModifiedAt: String,
        val optinPerformed: String,
        val phoneNumber: String,
  		val id: String,
  		val PatientWeight: String,
  		val shmiglibob: String,
        )
