(() => {
    const openers = new Map();
    let layers = [];

    window.gameLauncherDialogFocusSection = heading => {
        const body = heading.closest('.dialog-body');
        if (!body) return;

        const section = heading.closest('section[id]') ?? heading;
        body.scrollTop += section.getBoundingClientRect().top - body.getBoundingClientRect().top;
        heading.focus({ preventScroll: true });
    };

    function visibleLayers() {
        return Array.from(document.querySelectorAll('.editor-overlay'))
            .filter(layer => layer.querySelector('[role="dialog"]'))
            .sort((left, right) =>
                Number.parseInt(getComputedStyle(left).zIndex, 10) - Number.parseInt(getComputedStyle(right).zIndex, 10));
    }

    function dialogOf(layer) {
        return layer.querySelector('[role="dialog"]');
    }

    function focusDialog(layer) {
        const dialog = dialogOf(layer);
        const initial = dialog.querySelector('[data-dialog-initial]:not([disabled])');
        (initial || dialog).focus({ preventScroll: true });
    }

    function updateDialogs() {
        const previous = layers;
        const next = visibleLayers();
        const previousTop = previous.at(-1);
        const nextTop = next.at(-1);

        for (const layer of next) {
            if (!openers.has(layer)) {
                openers.set(layer, document.activeElement);
            }
        }

        layers = next;
        document.documentElement.classList.toggle('dialog-open', next.length > 0);

        if (previousTop !== nextTop) {
            const closed = previous.filter(layer => !next.includes(layer)).reverse();
            const returnTarget = closed
                .map(layer => openers.get(layer))
                .find(element => element?.isConnected && (!nextTop || dialogOf(nextTop).contains(element)));

            if (returnTarget && typeof returnTarget.focus === 'function') {
                returnTarget.focus({ preventScroll: true });
            } else if (nextTop) {
                focusDialog(nextTop);
            }
        } else if (nextTop && !dialogOf(nextTop).contains(document.activeElement)) {
            focusDialog(nextTop);
        }

        for (const layer of previous) {
            if (!next.includes(layer)) {
                openers.delete(layer);
            }
        }
    }

    document.addEventListener('keydown', event => {
        const layer = layers.at(-1);
        if (!layer) {
            return;
        }

        const dialog = dialogOf(layer);
        if (event.key === 'Escape') {
            const dismiss = dialog.querySelector('[data-dialog-dismiss]:not([disabled])');
            if (dismiss) {
                event.preventDefault();
                dismiss.click();
            }
            return;
        }

        if (event.key !== 'Tab') {
            return;
        }

        const focusable = Array.from(dialog.querySelectorAll(
            'a[href], button, input, select, textarea, details > summary:first-of-type, [tabindex]'
        )).filter(element => element.tabIndex >= 0 && !element.matches(':disabled')
            && !element.closest('[inert]') && element.getClientRects().length > 0
            && getComputedStyle(element).visibility === 'visible');

        if (focusable.length === 0) {
            event.preventDefault();
            dialog.focus({ preventScroll: true });
            return;
        }

        const active = document.activeElement;
        const boundary = event.shiftKey ? focusable[0] : focusable.at(-1);
        const beyondBoundary = event.shiftKey ? Node.DOCUMENT_POSITION_PRECEDING : Node.DOCUMENT_POSITION_FOLLOWING;
        if (!dialog.contains(active) || active === boundary || (boundary.compareDocumentPosition(active) & beyondBoundary)) {
            event.preventDefault();
            focusable[event.shiftKey ? focusable.length - 1 : 0].focus();
        }
    }, true);

    document.addEventListener('focusin', event => {
        const layer = layers.at(-1);
        if (layer && !dialogOf(layer).contains(event.target)) {
            focusDialog(layer);
        }
    });

    new MutationObserver(updateDialogs).observe(document.body, { childList: true, subtree: true });
    updateDialogs();
})();
