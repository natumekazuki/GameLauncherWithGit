Blazor.start({
    circuit: {
        // WebSocket is the only transport that sends Origin on every browser connection.
        configureSignalR: builder => builder.withUrl("/_blazor", { transport: 1 })
    }
});
