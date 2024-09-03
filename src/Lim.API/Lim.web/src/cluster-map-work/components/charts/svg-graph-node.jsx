import { useEffect, useMemo, useState } from 'react';

const PRESCALED_CANVAS_RESOLUTION = 64;
const MAX_PRESCALE_GLOBAL_SCALE_FACTOR = 2.5;

export class SvgGraphNode {
  iconSize;
  selectionIndicatorRadius;
  searchHighlightRadius;
  readyPromise;
  stateImages;
  svgImage;

  constructor(iconSvgUrl, iconSize, selectionIndicatorRadius, searchHighlightRadius, currentColor) {
    this.selectionIndicatorRadius = selectionIndicatorRadius;
    this.searchHighlightRadius = searchHighlightRadius;
    this.iconSize = iconSize;

    this.stateImages = [];
    for (let i = 0; i < 4; i++) {
      this.stateImages.push(new ScaleCachedSvgImage(null));
    }
    this.svgImage = new Image();

    this.readyPromise = this.loadImages(iconSvgUrl, currentColor);
  }

  draw(canvasContext, x, y, clipBoundingBox, globalScale, selected, highlighted, dimmed) {
    canvasContext.lineWidth = 10;

    if (selected) {
      this.drawSelectionIndicator(canvasContext, x, y);
    }

    if (highlighted) {
      this.drawHighlight(canvasContext, x, y);
    }

    this.stateImages[this.imageStateIndex(selected, dimmed)].draw(
      canvasContext,
      x,
      y,
      clipBoundingBox,
      globalScale
    );
  }

  drawSelectionIndicator(canvasContext, x, y) {
    canvasContext.strokeStyle = 'black';
    canvasContext.lineWidth = 0.75;
    canvasContext.setLineDash([3, 3]);
    canvasContext.beginPath();
    canvasContext.ellipse(
      x,
      y,
      this.selectionIndicatorRadius,
      this.selectionIndicatorRadius,
      0,
      0,
      2 * Math.PI
    );
    canvasContext.stroke();
    canvasContext.setLineDash([]);
  }

  drawHighlight(canvasContext, x, y) {
    canvasContext.fillStyle = 'rgba(255, 255, 201, 0.5)';
    canvasContext.beginPath();
    canvasContext.ellipse(
      x,
      y,
      this.searchHighlightRadius,
      this.searchHighlightRadius,
      0,
      0,
      2 * Math.PI
    );
    canvasContext.fill();
  }

  imageStateIndex(selected, dimmed) {
    return (selected ? 2 : 0) + (dimmed ? 1 : 0);
  }

  async loadImage(imageTemplate, currentColor, selected, dimmed) {
    const classNames = [...(selected ? ['selected'] : []), ...(dimmed ? ['dimmed'] : [])];

    this.stateImages[this.imageStateIndex(selected, dimmed)] = new ScaleCachedSvgImage(
      await imageTemplate.generateImage(classNames, currentColor, dimmed),
      this.iconSize
    );
  }

  async loadImages(iconSvgUrl, currentColor) {
    const imageTemplate = await SvgTemplate.fromUrl(iconSvgUrl);

    await this.loadImage(imageTemplate, currentColor, true, true);
    await this.loadImage(imageTemplate, currentColor, true, false);
    await this.loadImage(imageTemplate, currentColor, false, true);
    await this.loadImage(imageTemplate, currentColor, false, false);

    this.svgImage = await imageTemplate.generateImage([], currentColor, false);
  }
}

export class SvgTemplate {
  constructor(svgElement) {
    this.svgElement = svgElement;
  }

  static async fromUrl(url) {
    const svgElement = await new Promise(resolve => {
      fetch(url)
        .then(response => response.text())
        .then(
          responseText => new DOMParser().parseFromString(responseText, 'image/svg+xml').firstChild
        )
        .then(resolve);
    });

    return new SvgTemplate(svgElement);
  }

  async generateImage(classNames, currentColor, dimmed) {
    classNames = classNames || [];

    const generatedElement = this.svgElement.cloneNode(true);

    if (dimmed) {
      classNames.push('svg-icon-dimmed');
    }

    if (currentColor) {
      generatedElement.setAttribute('color', currentColor);
    }

    if (classNames.length) {
      generatedElement.setAttribute(
        'class',
        `${generatedElement.getAttribute('class')} ${classNames.join(' ')}`
      );
    }

    if (dimmed) {
      this.dimSvg(generatedElement);
    }

    return await new Promise(resolve => {
      const blobURL = `data:image/svg+xml;base64,${btoa(generatedElement.outerHTML)}`;
      const image = new Image();
      image.onload = () => resolve(image);
      image.src = blobURL;
    });
  }

  dimSvg(svgElement) {
    const encapsulatedGroup = document.createElementNS('http://www.w3.org/2000/svg', 'g');
    encapsulatedGroup.innerHTML = svgElement.innerHTML;

    const filterDefs = document.createElementNS('http://www.w3.org/2000/svg', 'defs');
    filterDefs.innerHTML = `
          <filter id="washout" filterUnits="objectBoundingBox"
                  x="0%" y="0%" width="100%" height="100%">
            <feFlood flood-color="#ffffff" flood-opacity="0.6" result="flood"/>
            <feComposite in="flood" in2="SourceAlpha" operator="atop" result="maskedflood"/>
            <feBlend mode="screen" in2="maskedflood" in="SourceGraphic"/>
          </filter>
        `;

    svgElement.innerHTML = '';
    svgElement.appendChild(filterDefs);
    svgElement.appendChild(encapsulatedGroup);
    svgElement.setAttribute('filter', `url(#washout)`);

    return svgElement;
  }
}

export class ScaleCachedSvgImage {
  prerenderedCanvas;
  svgImage;

  constructor(image, iconSize) {
    this.svgImage = image;

    this.iconSize = iconSize;

    this.prerenderedCanvas = document.createElement('canvas');
    this.prerenderedCanvas.height = PRESCALED_CANVAS_RESOLUTION;
    this.prerenderedCanvas.width = PRESCALED_CANVAS_RESOLUTION;
    if (this.svgImage) {
      this.prerenderedCanvas
        .getContext('2d')
        .drawImage(this.svgImage, 0, 0, PRESCALED_CANVAS_RESOLUTION, PRESCALED_CANVAS_RESOLUTION);
    }
  }

  draw(canvasContext, x, y, clipBoundingBox, globalScale) {
    // Some browsers (e.g Safari v12.1) won't optimize clipping to skip drawing off-screen
    // elements, so we do this for them ;)
    if (
      x > clipBoundingBox.left - this.iconSize &&
      y > clipBoundingBox.top - this.iconSize &&
      x < clipBoundingBox.left + clipBoundingBox.width + this.iconSize &&
      y < clipBoundingBox.top + clipBoundingBox.height + this.iconSize
    ) {
      // For small scale, we have many nodes but can suffice for lower resolution icons;
      // use that to speed up drawing.
      const imageSource =
        globalScale < MAX_PRESCALE_GLOBAL_SCALE_FACTOR ? this.prerenderedCanvas : this.svgImage;

      canvasContext.drawImage(
        imageSource,
        x - this.iconSize / 2,
        y - this.iconSize / 2,
        this.iconSize,
        this.iconSize
      );
    }
  }
}

export function SvgGraphNodeImage({
  iconUrl,
  iconSize,
  selectionIndicatorRadius,
  searchHighlightRadius,
  currentColor,
}) {
  const [imgHtml, setImgHtml] = useState('');

  const svgGraphNode = useMemo(() => {
    return new SvgGraphNode(
      iconUrl,
      iconSize,
      selectionIndicatorRadius,
      searchHighlightRadius,
      currentColor
    );
  }, [iconUrl, iconSize, selectionIndicatorRadius, searchHighlightRadius, currentColor]);

  useEffect(() => {
    setImgHtml('');
    svgGraphNode.readyPromise.then(() => {
      setImgHtml(svgGraphNode.svgImage.outerHTML);
    });
  }, [svgGraphNode]);

  return <div dangerouslySetInnerHTML={{ __html: imgHtml }} />;
}
