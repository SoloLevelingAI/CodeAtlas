// High-frequency gestures stay in the browser. Only the final pan enters navigation history.
(() => {
  let cleanup = null;
  let pendingPan = Promise.resolve();

  function bindMap(reference) {
    cleanup?.();
    const svg = document.getElementById('feature-map');
    const viewport = document.getElementById('map-viewport');
    if (!svg || !viewport) return;
    const controller = new AbortController();
    const options = {signal:controller.signal};
    let drag = null;
    let frame = 0;
    const point = event => new DOMPoint(event.clientX,event.clientY).matrixTransform(svg.getScreenCTM().inverse());
    const paint = () => {
      frame = 0;
      if (drag) viewport.setAttribute('transform',`translate(${drag.x} ${drag.y}) scale(${drag.zoom})`);
    };
    svg.addEventListener('pointerdown', event => {
      if (event.button !== 0 || event.target.closest('.map-node,.map-edge')) return;
      const matrix = viewport.transform.baseVal.consolidate()?.matrix;
      const start = point(event);
      drag = {pointer:event.pointerId,start,x:matrix?.e ?? 0,y:matrix?.f ?? 0,
        originX:matrix?.e ?? 0,originY:matrix?.f ?? 0,zoom:matrix?.a ?? 1,moved:false};
      svg.setPointerCapture(event.pointerId);
      svg.style.cursor = 'grabbing';
      event.preventDefault();
    },options);
    svg.addEventListener('pointermove', event => {
      if (!drag || event.pointerId !== drag.pointer) return;
      const current = point(event);
      drag.x = drag.originX + current.x - drag.start.x;
      drag.y = drag.originY + current.y - drag.start.y;
      drag.moved = true;
      if (!frame) frame = requestAnimationFrame(paint);
    },options);
    const finish = event => {
      if (!drag || event.pointerId !== drag.pointer) return;
      if (frame) cancelAnimationFrame(frame);
      paint();
      const completed = drag;
      drag = null;
      svg.style.cursor = '';
      if (svg.hasPointerCapture(completed.pointer)) svg.releasePointerCapture(completed.pointer);
      if (completed.moved) pendingPan = reference.invokeMethodAsync('CommitMapPan',completed.x,completed.y);
    };
    svg.addEventListener('pointerup',finish,options);
    svg.addEventListener('pointercancel',finish,options);
    svg.addEventListener('lostpointercapture',finish,options);
    cleanup = () => {controller.abort();if(frame) cancelAnimationFrame(frame);drag=null;svg.style.cursor='';};
  }

  window.atlasView = {
    bindMap,
    dispose: () => {cleanup?.();cleanup=null;},
    capture: async () => {
      await pendingPan;
      return {listScroll:document.getElementById('explorer-list')?.scrollTop ?? 0,
        detailScroll:document.getElementById('detail-body')?.scrollTop ?? 0};
    },
    restore: state => {
      for (const [id,key] of [['explorer-list','listScroll'],['detail-body','detailScroll']]) {
        const element = document.getElementById(id);
        if (element) element.scrollTop = state?.[key] ?? 0;
      }
    }
  };
})();
