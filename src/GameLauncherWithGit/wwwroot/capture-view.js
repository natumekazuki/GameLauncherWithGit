const observers = new WeakMap();
export function observeFeed(element, owner) {
    const listener = () => owner.invokeMethodAsync('FeedFollowing', element.scrollHeight - element.scrollTop - element.clientHeight < 40);
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
export function selectionStart(element) { return element.selectionStart; }
let previousFocus;
let previousBodyStyle;
export function focusEditor(owner) {
    previousFocus = document.activeElement;
    const dialog = document.getElementById('capture-editor');
    if (!dialog) return;
    const controls = () => [...dialog.querySelectorAll('button, input, select, textarea, [tabindex="0"]')]
        .filter(x => !x.disabled && x.getClientRects().length > 0 && getComputedStyle(x).visibility !== 'hidden');
    document.querySelector('.capture-page')?.setAttribute('inert', '');
    previousBodyStyle = { overflow: document.body.style.overflow, paddingRight: document.body.style.paddingRight };
    const scrollbar = window.innerWidth - document.documentElement.clientWidth;
    if (scrollbar > 0) document.body.style.paddingRight = `${parseFloat(getComputedStyle(document.body).paddingRight) + scrollbar}px`;
    document.body.style.overflow = 'hidden';
    const elements = controls();
    (elements.find(x => x.matches('input, select, textarea')) ?? elements[0])?.focus({ preventScroll: true });
    dialog.onkeydown = event => {
        if (event.key === 'Escape') { event.preventDefault(); owner.invokeMethodAsync('CloseEditorFromKeyboard'); }
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
    if (previousFocus?.isConnected) previousFocus.focus({ preventScroll: true });
    else if (previousFocus) document.querySelector('.capture-feed')?.focus({ preventScroll: true });
    previousFocus = null;
}
