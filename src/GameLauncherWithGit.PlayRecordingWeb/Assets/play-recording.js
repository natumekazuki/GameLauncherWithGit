const reconnectPanel = document.getElementById("components-reconnect-modal");
const reconnectStatus = document.getElementById("play-web-reconnect-status");
const reconnectError = reconnectPanel.querySelector(".play-web-reconnect-error");
let wasDisconnected = false;

// Blazor changes the panel's classes; keep the live region outside the hidden panel.
new MutationObserver(() => {
    const classes = reconnectPanel.classList;
    const reconnecting = classes.contains("components-reconnect-show");
    const failed = classes.contains("components-reconnect-failed") || classes.contains("components-reconnect-rejected");
    const disconnected = reconnecting || failed;
    if (disconnected && !wasDisconnected) {
        // The keyed workspace is replaced only after the panel has refreshed its connection version.
        const workspace = document.querySelector("#play-recording-content [data-play-recording-workspace]");
        if (workspace) {
            workspace.setAttribute("data-play-recording-stale", "");
            for (const detail of workspace.querySelectorAll("[data-play-recording-error-details][popover]")) {
                if (typeof detail.hidePopover === "function" && detail.matches(":popover-open")) detail.hidePopover();
            }
        }
    }
    wasDisconnected = disconnected;
    if (!failed) reconnectError.open = false;
    const message = reconnecting
        ? reconnectPanel.querySelector(".play-web-reconnecting").textContent
        : failed ? reconnectError.dataset.statusMessage : "";
    if (reconnectStatus.textContent !== message) {
        reconnectStatus.textContent = message;
    }
}).observe(reconnectPanel, { attributes: true, attributeFilter: ["class"] });

Blazor.start({
    circuit: {
        // WebSocket is the only transport that sends Origin on every browser connection.
        configureSignalR: builder => builder.withUrl("/_blazor", { transport: 1 })
    }
});
