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
}
export function scrollEnd(element) { element.scrollTop = element.scrollHeight; }
export function selectionStart(element) { return element.selectionStart; }
let previousFocus;
export function focusEditor(owner) {
    previousFocus = document.activeElement;
    const dialog = document.getElementById('capture-editor');
    if (!dialog) return;
    const controls = () => [...dialog.querySelectorAll('button, input, select, textarea, [tabindex="0"]')].filter(x => !x.disabled);
    controls()[0]?.focus();
    dialog.onkeydown = event => {
        if (event.key === 'Escape') { event.preventDefault(); owner.invokeMethodAsync('CloseEditorFromKeyboard'); }
        if (event.key !== 'Tab') return;
        const elements = controls(), first = elements[0], last = elements.at(-1);
        if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus(); }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus(); }
    };
}
export function restoreFocus() { if (previousFocus?.isConnected) previousFocus.focus(); previousFocus = null; }
