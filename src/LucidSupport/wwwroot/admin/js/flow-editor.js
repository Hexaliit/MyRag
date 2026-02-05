/**
 * LucidSupport Admin — Visual condition/workflow flow editor.
 * SVG connection lines between condition cards and field highlights.
 */

const FlowEditor = {
  /** Draw SVG connection lines between condition highlights and a minimap. */
  drawConnections(conditionsEl, minimapEl) {
    if (!conditionsEl || !minimapEl) return;

    const svg = minimapEl.querySelector('svg') || this.createSvg(minimapEl);
    svg.innerHTML = '';

    const cards = conditionsEl.querySelectorAll('.condition-card');
    cards.forEach((card, i) => {
      const highlight = card.dataset.highlight;
      if (!highlight) return;

      const target = minimapEl.querySelector(`[data-field="${highlight}"]`);
      if (!target) return;

      const line = document.createElementNS('http://www.w3.org/2000/svg', 'line');
      const cardRect = card.getBoundingClientRect();
      const targetRect = target.getBoundingClientRect();
      const containerRect = minimapEl.getBoundingClientRect();

      line.setAttribute('x1', cardRect.right - containerRect.left);
      line.setAttribute('y1', cardRect.top + cardRect.height / 2 - containerRect.top);
      line.setAttribute('x2', targetRect.left - containerRect.left);
      line.setAttribute('y2', targetRect.top + targetRect.height / 2 - containerRect.top);
      line.setAttribute('stroke', '#3b82f6');
      line.setAttribute('stroke-width', '1.5');
      line.setAttribute('stroke-dasharray', '4 2');
      line.setAttribute('opacity', '0.6');

      svg.appendChild(line);
    });
  },

  createSvg(container) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.style.cssText = 'position:absolute;top:0;left:0;width:100%;height:100%;pointer-events:none;';
    container.style.position = 'relative';
    container.appendChild(svg);
    return svg;
  }
};
