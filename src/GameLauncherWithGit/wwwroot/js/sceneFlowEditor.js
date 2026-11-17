const attachments = new WeakMap();
export function attach(root, owner) {
    let drag, suppressClick = false, activeDialog, previousFocus;
    const down = event => {
        const node = event.target.closest('[data-scene-node]');
        if (event.button !== 0 || !node || node.disabled || node.dataset.selected !== 'true' || node.closest('[inert]')) return;
        const surface = root.querySelector('[data-scene-surface]');
        drag = { node, id: node.dataset.sceneNode, x: Number(node.dataset.x), y: Number(node.dataset.y),
            startX: event.clientX, startY: event.clientY, left: node.style.left, top: node.style.top,
            scale: surface.getBoundingClientRect().width / surface.offsetWidth, pointerId: event.pointerId, moved: false };
        node.setPointerCapture(event.pointerId);
    };
    const move = event => {
        if (!drag || event.pointerId !== drag.pointerId) return;
        const dx = (event.clientX - drag.startX) / drag.scale, dy = (event.clientY - drag.startY) / drag.scale;
        if (Math.abs(dx) + Math.abs(dy) < 4 && !drag.moved) return;
        drag.moved = true;
        drag.node.style.left = `${parseFloat(drag.left) + dx}px`;
        drag.node.style.top = `${parseFloat(drag.top) + dy}px`;
    };
    const end = event => {
        if (!drag || event.pointerId !== drag.pointerId) return;
        const completed = drag; drag = null;
        completed.node.style.left = completed.left; completed.node.style.top = completed.top;
        if (event.type === 'pointercancel' || !completed.moved) return;
        suppressClick = true;
        setTimeout(() => { suppressClick = false; }, 0);
        owner.invokeMethodAsync('PositionSelectedScene', completed.id,
            completed.x + (event.clientX - completed.startX) / completed.scale,
            completed.y + (event.clientY - completed.startY) / completed.scale);
    };
    const click = event => { if (suppressClick) { suppressClick = false; event.preventDefault(); event.stopImmediatePropagation(); } };
    const controls = panel => [...panel.querySelectorAll('button,input,select,textarea,[tabindex="0"]')]
        .filter(el => !el.disabled && !el.closest('[inert]') && el.getClientRects().length > 0);
    const key = event => {
        const node = event.target.closest('[data-scene-node]');
        const delta = { ArrowLeft: [-20, 0], ArrowRight: [20, 0], ArrowUp: [0, -20], ArrowDown: [0, 20] }[event.key];
        if (node?.dataset.selected === 'true' && !node.disabled && delta) {
            event.preventDefault(); event.stopImmediatePropagation(); owner.invokeMethodAsync('MoveSelectedScene', ...delta); return;
        }
        if (event.key !== 'Tab') return;
        const panel = activeDialog ?? root, items = controls(panel), first = items[0], last = items.at(-1);
        if (event.shiftKey && document.activeElement === first) { event.preventDefault(); event.stopImmediatePropagation(); last?.focus(); }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); event.stopImmediatePropagation(); first?.focus(); }
    };
    const observer = new MutationObserver(() => {
        const next = [...root.querySelectorAll('.scene-subdialog')].filter(el => !el.closest('[inert]')).at(-1);
        if (next === activeDialog) return;
        if (next) { previousFocus = document.activeElement; activeDialog = next; controls(next)[0]?.focus({ preventScroll: true }); }
        else { activeDialog = undefined; if (previousFocus?.isConnected && !previousFocus.closest('[inert]')) previousFocus.focus({ preventScroll: true }); else focusSceneList(root); }
    });
    observer.observe(root, { childList: true, subtree: true });
    root.addEventListener('pointerdown', down); root.addEventListener('pointermove', move);
    root.addEventListener('pointerup', end); root.addEventListener('pointercancel', end);
    root.addEventListener('click', click, true); root.addEventListener('keydown', key, true);
    attachments.set(root, () => {
        observer.disconnect(); root.removeEventListener('pointerdown', down); root.removeEventListener('pointermove', move);
        root.removeEventListener('pointerup', end); root.removeEventListener('pointercancel', end);
        root.removeEventListener('click', click, true); root.removeEventListener('keydown', key, true);
    });
}
export function detach(root) { attachments.get(root)?.(); attachments.delete(root); }
export function revealSelected(root) {
    const node = root.querySelector('[data-selected="true"]'), canvas = root.querySelector('.scene-canvas');
    if (!node || !canvas) return;
    const rect = node.getBoundingClientRect(), bounds = canvas.getBoundingClientRect();
    if (rect.left < bounds.left || rect.right > bounds.right || rect.top < bounds.top || rect.bottom > bounds.bottom)
        canvas.scrollTo({ left: canvas.scrollLeft + rect.left - bounds.left - (canvas.clientWidth - rect.width) / 2,
            top: canvas.scrollTop + rect.top - bounds.top - (canvas.clientHeight - rect.height) / 2 });
}
export function fit(canvas, width, height) { return Math.min(canvas.clientWidth / width, canvas.clientHeight / height) * .96; }
export function resetScroll(canvas) { canvas.scrollTo({ left: 0, top: 0 }); }
export function focusSceneList(root) { root.querySelector('.scene-jump select')?.focus({ preventScroll: true }); }
export function focusTitle(root) { root.querySelector('input[aria-label="場面の主ラベル"]')?.focus({ preventScroll: true }); }
