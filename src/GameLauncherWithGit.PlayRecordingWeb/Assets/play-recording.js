const reconnectPanel = document.getElementById("components-reconnect-modal");
const reconnectStatus = document.getElementById("play-web-reconnect-status");

// Blazor changes the panel's classes; keep the live region outside the hidden panel.
new MutationObserver(() => {
    const classes = reconnectPanel.classList;
    const message = classes.contains("components-reconnect-show")
        ? reconnectPanel.querySelector(".play-web-reconnecting").textContent
        : classes.contains("components-reconnect-failed") || classes.contains("components-reconnect-rejected")
            ? reconnectPanel.querySelector(".play-web-reconnect-error").textContent
            : "";
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
