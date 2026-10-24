// Applies the stored light/dark choice before the first paint, so there is no flash of the wrong theme.
// Nothing here throws: storage may be unavailable (private windows, blocked site data).
try {
    const stored = localStorage.getItem("symphonist.theme");
    if (stored === "light" || stored === "dark") document.documentElement.dataset.theme = stored;
} catch {
    // Follow the system setting.
}
