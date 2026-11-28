const feeds = new WeakMap();
const endTolerance = 16;
const canScroll = element => element?.isConnected && element.getClientRects().length > 0
    && !element.closest("[hidden], [inert], [data-play-recording-stale]")
    && getComputedStyle(element).visibility === "visible";

// Do not treat an unmounted or obscured feed as a tail that may be replaced.
export function atEnd(element, scope) {
    return feeds.get(element)?.scope === scope && canScroll(element)
        && element.scrollHeight - element.clientHeight - element.scrollTop <= endTolerance;
}

// Notify only on a transition or on the first observation of this viewport.
export function observe(element, owner, scope) {
    if (!element || feeds.get(element)?.scope === scope) return;
    unobserve(element);
    const state = { scope, end: null, generation: 0, pending: null };
    const update = () => {
        const end = atEnd(element, scope);
        if (end === state.end) return;
        state.end = end;
        owner.invokeMethodAsync("FeedFollowing", scope, end).catch(() => {});
    };
    const invalidate = () => {
        state.pending = null;
        element.removeAttribute("data-scenario-feed-pending");
    };
    const onScroll = () => {
        if (state.pending && element.scrollTop < state.pending.top - 1 && !atEnd(element, scope)) invalidate();
        update();
    };
    const onKey = event => {
        if (event.target !== element && event.target.closest("input, textarea, select, [contenteditable]")) return;
        if (["ArrowUp", "PageUp", "Home", " "].includes(event.key)) invalidate();
    };
    const listeners = [["scroll", onScroll], ["wheel", invalidate], ["touchmove", invalidate], ["keydown", onKey]];
    state.remove = () => {
        invalidate();
        listeners.forEach(([event, listener]) => element.removeEventListener(event, listener));
    };
    state.update = update;
    feeds.set(element, state);
    listeners.forEach(([event, listener]) => element.addEventListener(event, listener, { passive: true }));
    update();
}

export function unobserve(element) {
    feeds.get(element)?.remove();
    feeds.delete(element);
}

// Capture the user's intent against the old DOM, then honor it once against the new DOM.
export function arm(element, scope) {
    if (!atEnd(element, scope)) return null;
    const state = feeds.get(element);
    const ticket = ++state.generation;
    state.pending = { ticket, top: element.scrollTop };
    element.setAttribute("data-scenario-feed-pending", "");
    return ticket;
}

export function apply(element, scope, ticket, follow = true) {
    const state = feeds.get(element);
    if (state?.scope !== scope || state.pending?.ticket !== ticket) return;
    state.pending = null;
    if (follow) scroll(element, true);
    element.removeAttribute("data-scenario-feed-pending");
}

export function scroll(element, end) {
    if (!canScroll(element)) return;
    element.scrollTop = end ? element.scrollHeight : 0;
    feeds.get(element)?.update();
}

export function position(element) { return element?.scrollTop ?? 0; }

export function restore(element, top) {
    if (!canScroll(element)) return;
    element.scrollTop = top;
    feeds.get(element)?.update();
}
