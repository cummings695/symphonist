// Light/dark toggle: the choice is the data-theme attribute on <html>, remembered per browser.
// Nothing here throws: storage may be unavailable (private windows, blocked site data).

const key = "symphonist.theme";

/** "light" or "dark": the chosen theme, else the system's. */
export function current() {
    try {
        const chosen = document.documentElement.dataset.theme;
        if (chosen === "light" || chosen === "dark") return chosen;
        return matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
    } catch {
        return "light";
    }
}

/** Applies and stores the theme; resolves to false when the browser refuses to store it (it is still applied). */
export function set(theme) {
    try {
        document.documentElement.dataset.theme = theme;
    } catch {
        return false;
    }
    try {
        localStorage.setItem(key, theme);
        return true;
    } catch {
        return false;
    }
}
