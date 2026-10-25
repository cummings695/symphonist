// Folded board stages (a header line at phone width, an upright strip on a desktop): remembers the stages the
// developer folded or unfolded, per browser.
// Nothing here throws: storage may be unavailable (private windows, blocked site data).

const key = "symphonist.board.folded";
const phoneQuery = "(max-width: 40rem)";

/** The stored choices as { columnId: folded }, or null when there are none (or they cannot be read). */
export function load() {
    try {
        const parsed = JSON.parse(localStorage.getItem(key) ?? "null");
        if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) return null;
        const choices = {};
        for (const [id, folded] of Object.entries(parsed))
            if (typeof folded === "boolean") choices[id] = folded;
        return choices;
    } catch {
        return null;
    }
}

/** Stores the choices; resolves to false when the browser refuses. */
export function save(choices) {
    try {
        localStorage.setItem(key, JSON.stringify(choices));
        return true;
    } catch {
        return false;
    }
}

/**
 * Whether the board is at phone width now; later changes call SetPhone on the component until that call fails
 * (the component was disposed or the circuit is gone), which removes the listener.
 */
export function watchPhone(component) {
    const media = matchMedia(phoneQuery);
    const changed = e => component.invokeMethodAsync("SetPhone", e.matches)
        .catch(() => media.removeEventListener("change", changed));
    media.addEventListener("change", changed);
    return media.matches;
}
