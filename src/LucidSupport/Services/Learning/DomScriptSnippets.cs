namespace LucidSupport.Services.Learning;

/// <summary>
///     Reusable JavaScript snippets for DOM extraction scripts.
/// </summary>
internal static class DomScriptSnippets
{
    public const string BuildStableSelectorFunction = """
        function buildStableSelector(el) {
            if (el.id) return '#' + el.id;
            if (el.name) {
                const tag = el.tagName.toLowerCase();
                const byName = document.querySelectorAll(tag + '[name="' + el.name + '"]');
                if (byName.length === 1) return tag + '[name="' + el.name + '"]';
            }

            const parts = [];
            let current = el;
            while (current && current !== document.body) {
                let sel = current.tagName.toLowerCase();
                if (current.id) { parts.unshift('#' + current.id); break; }
                const parent = current.parentElement;
                if (parent) {
                    const siblings = Array.from(parent.children).filter(c => c.tagName === current.tagName);
                    if (siblings.length > 1) {
                        sel += ':nth-of-type(' + (siblings.indexOf(current) + 1) + ')';
                    }
                }
                parts.unshift(sel);
                current = current.parentElement;
            }
            return parts.join(' > ');
        }
        """;

    public const string ErrorDetectionHelpers = """
        const ERROR_CLASS_RE = /\b(error|invalid|danger|has-error|is-invalid|field-error|ng-invalid|validation|warning|help-block)\b/i;

        function isVisibleElement(el) {
            if (!el) return false;
            if (el.hidden) return false;
            const cs = window.getComputedStyle(el);
            if (cs.display === 'none' || cs.visibility === 'hidden') return false;
            return el.offsetParent !== null;
        }

        function elementHasErrorClass(el) {
            return !!el && ERROR_CLASS_RE.test((el.className || '').toString());
        }

        function stableSelectorOrTag(el) {
            if (typeof buildStableSelector === 'function') return buildStableSelector(el);
            if (el.id) return '#' + el.id;
            return (el.tagName || '').toLowerCase();
        }

        function findFieldErrorMessage(el) {
            const errId = el.getAttribute('aria-errormessage');
            if (errId) {
                const errEl = document.getElementById(errId);
                if (isVisibleElement(errEl) && (errEl.textContent || '').trim()) {
                    return { text: errEl.textContent.trim(), selector: '#' + errId, element: errEl };
                }
            }

            const descBy = el.getAttribute('aria-describedby');
            if (descBy) {
                for (const id of descBy.split(/\s+/)) {
                    const ref = document.getElementById(id);
                    if (isVisibleElement(ref) && elementHasErrorClass(ref)) {
                        const text = (ref.textContent || '').trim();
                        if (text) return { text, selector: '#' + id, element: ref };
                    }
                }
            }

            const sib = el.nextElementSibling;
            if (isVisibleElement(sib) && elementHasErrorClass(sib)) {
                const text = (sib.textContent || '').trim();
                if (text) return { text, selector: stableSelectorOrTag(sib), element: sib };
            }

            const parent = el.parentElement;
            if (parent) {
                const errChild = parent.querySelector('[role="alert"], .error-message, .field-error, .validation-message, .invalid-feedback, .text-danger');
                if (isVisibleElement(errChild)) {
                    const text = (errChild.textContent || '').trim();
                    if (text) return { text, selector: stableSelectorOrTag(errChild), element: errChild };
                }
            }

            return null;
        }

        function isFieldInErrorState(el) {
            if (!el) return false;
            const hasInvalidValidity = el.validity && el.validity.valid === false;
            if (hasInvalidValidity) return true;
            if (el.getAttribute('aria-invalid') === 'true') return true;
            if (elementHasErrorClass(el)) return true;
            if (elementHasErrorClass(el.parentElement)) return true;
            return findFieldErrorMessage(el) !== null;
        }
        """;
}
