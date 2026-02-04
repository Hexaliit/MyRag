namespace CodeSummarizer.Mermaid.Jint;

/// <summary>
///     Minimal DOM polyfill strings for running mermaid.js in Jint.
///     Mermaid's parse-only path touches few DOM APIs — mainly DOMParser for
///     text sanitization. Rendering (d3, SVG) is not triggered by parse().
/// </summary>
internal static class MermaidDomStubs
{
    /// <summary>
    ///     JavaScript polyfill that provides minimal DOM stubs for mermaid.js.
    ///     Must be executed in the Jint engine before loading the mermaid bundle.
    /// </summary>
    internal static string GetPolyfill()
    {
        return """
               // === Minimal DOM stubs for mermaid.js parse-only mode ===

               // Element stub
               function StubElement(tag) {
                   this.tagName = tag || 'DIV';
                   this.children = [];
                   this.childNodes = [];
                   this.attributes = {};
                   this.style = {};
                   this.classList = {
                       add: function() {},
                       remove: function() {},
                       contains: function() { return false; },
                       toggle: function() {}
                   };
                   this.innerHTML = '';
                   this.textContent = '';
                   this.id = '';
                   this.className = '';
                   this.parentNode = null;
                   this.parentElement = null;
                   this.nextSibling = null;
                   this.previousSibling = null;
                   this.firstChild = null;
                   this.lastChild = null;
                   this.ownerDocument = null;
                   this.nodeType = 1;
               }
               StubElement.prototype.getAttribute = function(n) { return this.attributes[n] || null; };
               StubElement.prototype.setAttribute = function(n, v) { this.attributes[n] = v; };
               StubElement.prototype.removeAttribute = function(n) { delete this.attributes[n]; };
               StubElement.prototype.hasAttribute = function(n) { return n in this.attributes; };
               StubElement.prototype.appendChild = function(c) { this.children.push(c); this.childNodes.push(c); c.parentNode = this; return c; };
               StubElement.prototype.removeChild = function(c) { return c; };
               StubElement.prototype.insertBefore = function(n, r) { this.children.push(n); return n; };
               StubElement.prototype.replaceChild = function(n, o) { return o; };
               StubElement.prototype.cloneNode = function(d) { return new StubElement(this.tagName); };
               StubElement.prototype.querySelector = function(s) { return null; };
               StubElement.prototype.querySelectorAll = function(s) { return []; };
               StubElement.prototype.getElementsByTagName = function(t) { return []; };
               StubElement.prototype.getElementsByClassName = function(c) { return []; };
               StubElement.prototype.getElementById = function(id) { return null; };
               StubElement.prototype.addEventListener = function() {};
               StubElement.prototype.removeEventListener = function() {};
               StubElement.prototype.dispatchEvent = function() { return true; };
               StubElement.prototype.getBoundingClientRect = function() { return { top: 0, left: 0, bottom: 0, right: 0, width: 0, height: 0 }; };
               StubElement.prototype.matches = function(s) { return false; };
               StubElement.prototype.closest = function(s) { return null; };
               StubElement.prototype.remove = function() {};
               StubElement.prototype.contains = function() { return false; };
               StubElement.prototype.focus = function() {};
               StubElement.prototype.blur = function() {};

               // Document stub
               var _stubBody = new StubElement('BODY');
               var _stubHead = new StubElement('HEAD');
               var _stubDocEl = new StubElement('HTML');
               _stubDocEl.appendChild(_stubHead);
               _stubDocEl.appendChild(_stubBody);

               if (typeof globalThis.document === 'undefined') {
                   globalThis.document = {
                       body: _stubBody,
                       head: _stubHead,
                       documentElement: _stubDocEl,
                       createElement: function(tag) { return new StubElement(tag); },
                       createElementNS: function(ns, tag) { return new StubElement(tag); },
                       createTextNode: function(text) { var e = new StubElement('#text'); e.textContent = text; e.nodeType = 3; return e; },
                       createComment: function(text) { return new StubElement('#comment'); },
                       createDocumentFragment: function() { return new StubElement('#fragment'); },
                       querySelector: function(s) { return null; },
                       querySelectorAll: function(s) { return []; },
                       getElementById: function(id) { return null; },
                       getElementsByTagName: function(t) { return []; },
                       getElementsByClassName: function(c) { return []; },
                       addEventListener: function() {},
                       removeEventListener: function() {},
                       createEvent: function() { return { initEvent: function() {} }; },
                       implementation: {
                           createHTMLDocument: function(title) { return globalThis.document; }
                       },
                       readyState: 'complete',
                       cookie: '',
                       title: '',
                       baseURI: 'about:blank',
                       URL: 'about:blank',
                       defaultView: null,
                       nodeType: 9,
                       childNodes: [_stubDocEl]
                   };
               }

               // DOMParser stub - used by mermaid for text sanitization
               if (typeof globalThis.DOMParser === 'undefined') {
                   globalThis.DOMParser = function() {};
                   globalThis.DOMParser.prototype.parseFromString = function(str, type) {
                       var doc = {
                           body: new StubElement('BODY'),
                           documentElement: new StubElement('HTML'),
                           querySelector: function(s) { return null; },
                           querySelectorAll: function(s) { return []; },
                           getElementById: function(id) { return null; },
                           getElementsByTagName: function(t) { return []; },
                           childNodes: [],
                           nodeType: 9
                       };
                       doc.body.textContent = str;
                       doc.body.innerHTML = str;
                       return doc;
                   };
               }

               // XMLSerializer stub
               if (typeof globalThis.XMLSerializer === 'undefined') {
                   globalThis.XMLSerializer = function() {};
                   globalThis.XMLSerializer.prototype.serializeToString = function(node) { return ''; };
               }

               // Window / self / navigator stubs
               if (typeof globalThis.window === 'undefined') {
                   globalThis.window = globalThis;
               }
               if (typeof globalThis.self === 'undefined') {
                   globalThis.self = globalThis;
               }
               if (typeof globalThis.navigator === 'undefined') {
                   globalThis.navigator = { userAgent: 'Jint', language: 'en-US', languages: ['en-US'] };
               }

               // Console - no-op (could redirect to .NET logging)
               if (typeof globalThis.console === 'undefined') {
                   globalThis.console = {
                       log: function() {},
                       warn: function() {},
                       error: function() {},
                       info: function() {},
                       debug: function() {},
                       trace: function() {},
                       dir: function() {},
                       table: function() {},
                       group: function() {},
                       groupEnd: function() {},
                       time: function() {},
                       timeEnd: function() {},
                       assert: function() {}
                   };
               }

               // Location stub
               if (typeof globalThis.location === 'undefined') {
                   globalThis.location = {
                       href: 'about:blank',
                       protocol: 'about:',
                       host: '',
                       hostname: '',
                       port: '',
                       pathname: 'blank',
                       search: '',
                       hash: '',
                       origin: 'null'
                   };
               }

               // Timer stubs
               if (typeof globalThis.setTimeout === 'undefined') {
                   globalThis.setTimeout = function(fn) { if (typeof fn === 'function') fn(); return 0; };
               }
               if (typeof globalThis.clearTimeout === 'undefined') {
                   globalThis.clearTimeout = function() {};
               }
               if (typeof globalThis.setInterval === 'undefined') {
                   globalThis.setInterval = function() { return 0; };
               }
               if (typeof globalThis.clearInterval === 'undefined') {
                   globalThis.clearInterval = function() {};
               }
               if (typeof globalThis.requestAnimationFrame === 'undefined') {
                   globalThis.requestAnimationFrame = function(fn) { if (typeof fn === 'function') fn(0); return 0; };
               }
               if (typeof globalThis.cancelAnimationFrame === 'undefined') {
                   globalThis.cancelAnimationFrame = function() {};
               }

               // URL stub
               if (typeof globalThis.URL === 'undefined') {
                   globalThis.URL = function(url) {
                       this.href = url || '';
                       this.origin = '';
                       this.protocol = '';
                       this.host = '';
                       this.hostname = '';
                       this.port = '';
                       this.pathname = '';
                       this.search = '';
                       this.hash = '';
                   };
                   globalThis.URL.createObjectURL = function() { return 'blob:null'; };
                   globalThis.URL.revokeObjectURL = function() {};
               }

               // Blob stub
               if (typeof globalThis.Blob === 'undefined') {
                   globalThis.Blob = function(parts, options) {
                       this.size = 0;
                       this.type = (options && options.type) || '';
                   };
               }

               // CustomEvent stub
               if (typeof globalThis.CustomEvent === 'undefined') {
                   globalThis.CustomEvent = function(type, params) {
                       this.type = type;
                       this.detail = params ? params.detail : null;
                   };
               }

               // MutationObserver stub
               if (typeof globalThis.MutationObserver === 'undefined') {
                   globalThis.MutationObserver = function(callback) {};
                   globalThis.MutationObserver.prototype.observe = function() {};
                   globalThis.MutationObserver.prototype.disconnect = function() {};
                   globalThis.MutationObserver.prototype.takeRecords = function() { return []; };
               }

               // ResizeObserver stub
               if (typeof globalThis.ResizeObserver === 'undefined') {
                   globalThis.ResizeObserver = function(callback) {};
                   globalThis.ResizeObserver.prototype.observe = function() {};
                   globalThis.ResizeObserver.prototype.unobserve = function() {};
                   globalThis.ResizeObserver.prototype.disconnect = function() {};
               }

               // IntersectionObserver stub
               if (typeof globalThis.IntersectionObserver === 'undefined') {
                   globalThis.IntersectionObserver = function(callback) {};
                   globalThis.IntersectionObserver.prototype.observe = function() {};
                   globalThis.IntersectionObserver.prototype.unobserve = function() {};
                   globalThis.IntersectionObserver.prototype.disconnect = function() {};
               }

               // getComputedStyle stub
               if (typeof globalThis.getComputedStyle === 'undefined') {
                   globalThis.getComputedStyle = function() {
                       return {
                           getPropertyValue: function() { return ''; }
                       };
                   };
               }

               // matchMedia stub
               if (typeof globalThis.matchMedia === 'undefined') {
                   globalThis.matchMedia = function() {
                       return {
                           matches: false,
                           media: '',
                           addEventListener: function() {},
                           removeEventListener: function() {},
                           addListener: function() {},
                           removeListener: function() {}
                       };
                   };
               }

               // Performance stub
               if (typeof globalThis.performance === 'undefined') {
                   globalThis.performance = {
                       now: function() { return Date.now(); },
                       mark: function() {},
                       measure: function() {},
                       getEntriesByName: function() { return []; },
                       getEntriesByType: function() { return []; }
                   };
               }

               // SVGElement stub (for mermaid rendering stubs)
               if (typeof globalThis.SVGElement === 'undefined') {
                   globalThis.SVGElement = StubElement;
               }
               if (typeof globalThis.HTMLElement === 'undefined') {
                   globalThis.HTMLElement = StubElement;
               }

               // btoa / atob stubs
               if (typeof globalThis.btoa === 'undefined') {
                   globalThis.btoa = function(s) { return s; };
               }
               if (typeof globalThis.atob === 'undefined') {
                   globalThis.atob = function(s) { return s; };
               }

               // fetch stub (should not be called during parse-only)
               if (typeof globalThis.fetch === 'undefined') {
                   globalThis.fetch = function() {
                       return { then: function() { return { catch: function() { return { finally: function() {} }; } }; } };
                   };
               }

               // TextEncoder / TextDecoder stubs
               if (typeof globalThis.TextEncoder === 'undefined') {
                   globalThis.TextEncoder = function() {};
                   globalThis.TextEncoder.prototype.encode = function(s) {
                       var arr = [];
                       for (var i = 0; i < s.length; i++) arr.push(s.charCodeAt(i) & 0xFF);
                       return arr;
                   };
               }
               if (typeof globalThis.TextDecoder === 'undefined') {
                   globalThis.TextDecoder = function() {};
                   globalThis.TextDecoder.prototype.decode = function(arr) {
                       if (!arr) return '';
                       var s = '';
                       for (var i = 0; i < arr.length; i++) s += String.fromCharCode(arr[i]);
                       return s;
                   };
               }
               """;
    }
}