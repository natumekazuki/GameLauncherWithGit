const observers = new WeakMap();
export function observeFeed(element, owner) {
    const listener = () => {
        const scope = element.dataset.captureScope;
        if (scope) owner.invokeMethodAsync('FeedFollowing', scope, element.scrollHeight - element.scrollTop - element.clientHeight < 40);
    };
    element.addEventListener('scroll', listener, { passive: true });
    observers.set(element, listener);
}
export function unobserveFeed(element) {
    const listener = observers.get(element);
    if (listener) element.removeEventListener('scroll', listener);
    observers.delete(element);
    restoreFocus();
}
export function scrollFeed(element, end) { element.scrollTop = end ? element.scrollHeight : 0; }
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
export function focusEditor(owner, scope) {
    const dialog = document.getElementById('capture-editor');
    if (!dialog) return;
    const controls = () => [...dialog.querySelectorAll('button, input, select, textarea, [tabindex="0"]')]
        .filter(x => !x.disabled && x.getClientRects().length > 0 && getComputedStyle(x).visibility !== 'hidden');
    // A finish confirmation can replace the range editor and back again; restore the first opener.
    if (!previousBodyStyle) {
        previousFocus = document.activeElement;
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
