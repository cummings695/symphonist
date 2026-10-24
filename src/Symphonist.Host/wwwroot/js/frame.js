// The frame's focus and scroll moves that Blazor cannot make itself: focusing an element it holds no reference to
// (a sidebar row, the page heading) and resetting the page's own scroll container. Nothing here throws.

/** Focuses the first element found for the first selector that matches; resolves to false when none does. */
export function focus(selectors) {
    try {
        for (const selector of selectors) {
            const element = document.querySelector(selector);
            if (element) {
                element.focus();
                return true;
            }
        }
    } catch {
        // E.g. an invalid selector: focus stays where it is.
    }
    return false;
}

/** Scrolls the element (the frame's scrolling page area) back to the top. */
export function scrollToTop(element) {
    try {
        element.scrollTop = 0;
    } catch {
        // The element is gone: there is nothing to scroll.
    }
}
