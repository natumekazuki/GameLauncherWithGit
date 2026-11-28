const commands = new WeakMap();
export function observeFeedCommands(element, owner, menu) {
    unobserveFeedCommands(element);
    const events = new AbortController();
    const disposeMenu = observeRowMenu(element, owner, menu, { signal: events.signal });
    commands.set(element, () => { events.abort(); disposeMenu(); });
}
export function unobserveFeedCommands(element) {
    const dispose = commands.get(element);
    if (!dispose) return;
    dispose();
    commands.delete(element);
    restoreFocus();
}

/** Routes the PC row's context gestures through one popover without intercepting ordinary scrolling. */
function observeRowMenu(feed, owner, menu, options) {
    let active, touch, consumed;
    const action = menu.querySelector('[data-capture-row-delete]');
    const hit = target => target instanceof Element ? target.closest('[data-capture-row]') : null;
    const notify = (scope, open) => owner.invokeMethodAsync('RowMenuVisibility', scope, open).catch(() => {});
    const valid = (row, scope) => scope && scope === feed.dataset.captureScope &&
        feed.dataset.captureRowMenuBlocked !== 'true' && feed.contains(row) && !row.disabled && row.getClientRects().length;

    function close(restore = false) {
        const selection = active;
        active = null;
        if (menu.matches(':popover-open')) menu.hidePopover();
        if (selection) notify(selection.scope, false);
        const row = selection?.row;
        row?.removeAttribute('aria-expanded');
        if (restore && row?.isConnected && !row.disabled) row.focus({ preventScroll: true });
    }

    function open(row, scope, x, y) {
        if (!valid(row, scope)) return false;
        if (!active) notify(scope, true);
        active?.row.removeAttribute('aria-expanded');
        active = { row, scope, id: row.dataset.captureRow, feedTop: feed.scrollTop, windowTop: window.scrollY };
        row.setAttribute('aria-expanded', 'true');
        if (!menu.matches(':popover-open')) menu.showPopover();
        const box = menu.getBoundingClientRect();
        const anchor = row.getBoundingClientRect();
        // Keyboard requests anchor to the row. Pointer requests stay near where the row was pressed.
        menu.style.left = `${Math.max(6, Math.min(x ?? anchor.left, innerWidth - box.width - 6))}px`;
        menu.style.top = `${Math.max(6, Math.min(y ?? anchor.bottom, innerHeight - box.height - 6))}px`;
        action.focus({ preventScroll: true });
        return true;
    }

    function cancelTouch() {
        clearTimeout(touch?.timer);
        touch = null;
    }

    feed.addEventListener('contextmenu', event => {
        const row = hit(event.target);
        if (!row || !valid(row, feed.dataset.captureScope)) return;
        event.preventDefault();
        if (event.pointerType === 'touch' || touch?.row === row) return;
        // Some keyboard-generated contextmenu events have no pointer coordinates.
        const hasPoint = event.button === 2 || event.clientX !== 0 || event.clientY !== 0;
        open(row, feed.dataset.captureScope, hasPoint ? event.clientX : null, hasPoint ? event.clientY : null);
    }, options);
    feed.addEventListener('keydown', event => {
        if (event.key !== 'ContextMenu' && !(event.shiftKey && event.key === 'F10')) return;
        const row = hit(event.target);
        if (row && open(row, feed.dataset.captureScope)) event.preventDefault();
    }, options);

    feed.addEventListener('pointerdown', event => {
        cancelTouch();
        if (event.pointerType !== 'touch' || !event.isPrimary) return;
        consumed = null;
        const row = hit(event.target), scope = feed.dataset.captureScope;
        if (!row || !valid(row, scope)) return;
        const gesture = { row, scope, id: event.pointerId, x: event.clientX, y: event.clientY };
        gesture.timer = setTimeout(() => {
            if (touch === gesture && open(row, scope, gesture.x, gesture.y + 12)) consumed = gesture;
        }, 550);
        touch = gesture;
    }, { ...options, passive: true });
    // A touch that opened the menu began outside it. Dismiss on the next outside press, not on that release.
    document.addEventListener('pointerdown', event => {
        if (active && !menu.contains(event.target)) close();
    }, { ...options, capture: true, passive: true });
    document.addEventListener('pointermove', event => {
        if (touch?.id === event.pointerId && Math.hypot(event.clientX - touch.x, event.clientY - touch.y) > 12) cancelTouch();
    }, { ...options, passive: true });
    for (const type of ['pointerup', 'pointercancel']) document.addEventListener(type, event => {
        if (touch?.id === event.pointerId) cancelTouch();
        // Suppress only the synthetic click from the pointer that opened the long-press menu.
        const released = consumed;
        if (released?.id === event.pointerId) setTimeout(() => { if (consumed === released) consumed = null; }, 450);
    }, { ...options, passive: true });
    document.addEventListener('click', event => {
        if (!consumed || !(event.pointerType === 'touch' || event.sourceCapabilities?.firesTouchEvents) ||
            Math.hypot(event.clientX - consumed.x, event.clientY - consumed.y) > 24) return;
        consumed = null;
        event.preventDefault();
        event.stopImmediatePropagation();
    }, { ...options, capture: true });
    document.addEventListener('scroll', () => {
        cancelTouch();
        // A queued scroll event can arrive after opening. Dismiss only if the anchor has actually moved.
        if (active && (active.feedTop !== feed.scrollTop || active.windowTop !== window.scrollY)) close();
    }, { ...options, capture: true, passive: true });
    window.addEventListener('blur', () => { cancelTouch(); close(); }, options);

    menu.addEventListener('keydown', event => {
        if (event.key === 'Escape' || event.key === 'Tab') { event.preventDefault(); close(true); }
    }, options);
    menu.addEventListener('toggle', () => { if (!menu.matches(':popover-open')) close(); }, options);
    action.addEventListener('click', () => {
        const selection = active;
        close(true);
        if (selection && valid(selection.row, selection.scope))
            owner.invokeMethodAsync('OpenRowDeletionFromMenuAsync', selection.scope, selection.id);
    }, options);

    // A route change or another operation can retire the row while the native popover is open.
    const changes = new MutationObserver(() => { if (active && !valid(active.row, active.scope)) close(); });
    changes.observe(feed, { childList: true, subtree: true, attributes: true,
        attributeFilter: ['data-capture-scope', 'data-capture-row-menu-blocked', 'disabled'] });
    return () => { cancelTouch(); close(); changes.disconnect(); };
}
export function focusTextDetail(element) {
    element.focus({ preventScroll: true });
    element.onkeydown = event => {
        // Wide panes stay keyboard-accessible alongside the feed; the narrow overlay keeps Tab inside.
        if (event.key !== 'Tab' || !matchMedia('(max-width: 900px)').matches) return;
        const controls = [...element.querySelectorAll('button, input, select, textarea, a[href]')]
            .filter(control => !control.disabled && control.getClientRects().length > 0);
        const first = controls[0], last = controls.at(-1);
        if (event.shiftKey && [element, first].includes(document.activeElement)) { event.preventDefault(); last?.focus(); }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus(); }
    };
}
export function focusTextDetailOpener(id) {
    // The item may be outside the current filter or an older page.
    const row = id ? document.getElementById(id) : null;
    const target = row?.getClientRects().length ? row : document.querySelector('.capture-feed');
    target?.focus({ preventScroll: true });
}
export function selectionStart(element) { return element.selectionStart; }
export function focusEditorContent() {
    const dialog = document.getElementById('capture-editor');
    const control = dialog?.querySelector('input:not(:disabled), select:not(:disabled), button:not(:disabled)');
    control?.focus({ preventScroll: true });
}
let previousFocus;
let previousBodyStyle;
export function focusEditor(owner, scope, openerId) {
    const dialog = document.getElementById('capture-editor');
    if (!dialog) return;
    const controls = () => [...dialog.querySelectorAll('button, input, select, textarea, [tabindex="0"]')]
        .filter(x => !x.disabled && x.getClientRects().length > 0 && getComputedStyle(x).visibility !== 'hidden');
    // A finish confirmation can replace the range editor and back again; restore the first opener.
    if (!previousBodyStyle) {
        previousFocus = (openerId && document.getElementById(openerId)) || document.activeElement;
        document.querySelector('.capture-page')?.setAttribute('inert', '');
        previousBodyStyle = { overflow: document.body.style.overflow, paddingRight: document.body.style.paddingRight };
        const scrollbar = window.innerWidth - document.documentElement.clientWidth;
        if (scrollbar > 0) document.body.style.paddingRight = `${parseFloat(getComputedStyle(document.body).paddingRight) + scrollbar}px`;
        document.body.style.overflow = 'hidden';
    }
    const elements = controls();
    (elements.find(x => x.matches('input, select, textarea')) ?? elements[0])?.focus({ preventScroll: true });
    dialog.onkeydown = event => {
        if (event.key === 'Escape') {
            if (dialog.querySelector('.capture-failure-popover:popover-open')) return;
            event.preventDefault();
            owner.invokeMethodAsync('CloseEditorFromKeyboard', scope);
        }
        if (event.key !== 'Tab') return;
        const elements = controls(), first = elements[0], last = elements.at(-1);
        if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus(); }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus(); }
    };
}
export function focusSettingsTab() {
    document.querySelector('.capture-settings-tabs [aria-pressed="true"]')?.focus({ preventScroll: true });
}
export function restoreFocus() {
    document.querySelector('.capture-page')?.removeAttribute('inert');
    if (previousBodyStyle) Object.assign(document.body.style, previousBodyStyle);
    previousBodyStyle = null;
    if (previousFocus?.isConnected && !previousFocus.disabled) previousFocus.focus({ preventScroll: true });
    else if (previousFocus) document.querySelector('.capture-feed')?.focus({ preventScroll: true });
    previousFocus = null;
}
