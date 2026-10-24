// Reacts to blazor.web.js reconnect states. The circuit lives in host memory, so after a host restart reconnecting is
// rejected (unknown circuit): the page reloads, and the persisted sign-in cookie keeps the developer signed in.
const modal = document.getElementById("components-reconnect-modal");
modal.addEventListener("components-reconnect-state-changed", onStateChanged);
document.getElementById("components-reload-button").addEventListener("click", () => location.reload());

function onStateChanged(event) {
    const state = event.detail.state;
    if (state === "show" || state === "retrying") {
        modal.dataset.state = "retrying";
        if (!modal.open) modal.showModal();
    } else if (state === "hide") {
        modal.close();
    } else if (state === "failed") {
        // Retries exhausted: offer a reload, and keep trying whenever the developer comes back to the tab.
        modal.dataset.state = "failed";
        if (!modal.open) modal.showModal();
        document.addEventListener("visibilitychange", retryWhenVisible);
    } else if (state === "rejected") {
        modal.dataset.state = "rejected";
        if (!modal.open) modal.showModal();
        location.reload();
    }
}

async function retryWhenVisible() {
    if (document.visibilityState !== "visible") return;
    document.removeEventListener("visibilitychange", retryWhenVisible);
    try {
        if (!(await Blazor.reconnect())) onStateChanged({ detail: { state: "rejected" } });
    } catch {
        document.addEventListener("visibilitychange", retryWhenVisible);
    }
}
