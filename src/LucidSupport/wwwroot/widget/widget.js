function X(t){async function n(i){try{let c=await fetch(`${t}/api/support/page?url=${encodeURIComponent(i)}`,{credentials:"omit"});return c.ok?c.json():null}catch{return null}}async function o(i){let c=await fetch(`${t}/api/help/contextual`,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(i),keepalive:!0,credentials:"omit"});if(!c.ok)throw new Error(`Help request failed: ${c.status}`);return c.json()}async function s(i,c,u){let d=await fetch(`${t}/api/help/contextual?stream=true`,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(i),credentials:"omit"});if(!d.ok)throw new Error(`Stream request failed: ${d.status}`);if(!d.body){let h=await d.text();c(h),u();return}let f=d.body.getReader(),y=new TextDecoder;try{for(;;){let{done:h,value:x}=await f.read();if(h)break;c(y.decode(x,{stream:!0}))}}finally{f.releaseLock(),u()}}function l(i,c={}){if(!navigator.sendBeacon)return;let u=JSON.stringify({event:i,...c,ts:Date.now()});navigator.sendBeacon(`${t}/api/analytics`,u)}return{loadPageModel:n,askForHelp:o,askStreaming:s,trackEvent:l}}function J(t){let{trackedSelectors:n,fieldIdleMs:o,onFieldChange:s,onFieldIdle:l,onSubmit:i}=t,c=new Set(n),u=new Map,d=new Map,f=null,y=null,h=!1;function x(e){let r=e.target;if(!(r instanceof Element))return;let a=m(r);a&&(f=a,L(),y=setTimeout(()=>l(a),o),v(a,r))}function E(e){let r=e.target;if(!(r instanceof Element))return;let a=m(r);a&&setTimeout(()=>{f===a&&(f=null),L(),v(a,r)},300)}function S(e){e.target instanceof HTMLFormElement&&i()}let M=new MutationObserver(e=>{h||(h=!0,_(()=>{b(e),h=!1}))});function b(e){let r=new Set;for(let a of e){if(a.type==="attributes"&&a.target instanceof Element){let p=m(a.target);p&&r.add(p)}if(a.type==="childList"){for(let p of a.addedNodes)p instanceof Element&&k(p,r);for(let p of a.removedNodes)p instanceof Element&&k(p,r)}}for(let a of r){let p=document.querySelector(a);p&&v(a,p)}}let g=new IntersectionObserver(e=>{for(let r of e){let a=T.get(r.target);a&&d.set(a,r.isIntersecting)}},{threshold:.1}),T=new Map;function v(e,r){let a={hasValue:F(r),hasError:z(r),errorText:A(r),hasFocus:f===e},p=u.get(e);(!p||w(p,a))&&(u.set(e,a),s(e,a))}function w(e,r){return e.hasValue!==r.hasValue||e.hasError!==r.hasError||e.errorText!==r.errorText||e.hasFocus!==r.hasFocus}function F(e){return e instanceof HTMLInputElement||e instanceof HTMLTextAreaElement?e.value.length>0:e instanceof HTMLSelectElement?e.selectedIndex>0:!1}function z(e){if(e instanceof HTMLInputElement&&!e.validity.valid||e instanceof HTMLTextAreaElement&&!e.validity.valid||e instanceof HTMLSelectElement&&!e.validity.valid||e.getAttribute("aria-invalid")==="true")return!0;let r=e.className.toString().toLowerCase();if(/\b(error|invalid|danger|has-error|is-invalid|field-error|ng-invalid)\b/.test(r))return!0;let a=e.parentElement;if(a){let p=a.className.toString().toLowerCase();if(/\b(error|invalid|has-error|field-error)\b/.test(p))return!0}return!1}function A(e){let r=e.getAttribute("aria-errormessage");if(r){let I=document.getElementById(r);if(I&&C(I))return I.textContent?.trim()||null}let a=e.getAttribute("aria-describedby");if(a)for(let I of a.split(/\s+/)){let $=document.getElementById(I);if($&&C($)&&/error|invalid|help/.test($.className.toLowerCase()))return $.textContent?.trim()||null}let p=e.nextElementSibling;if(p&&C(p)&&/error|invalid|validation|field-error/.test(p.className.toLowerCase()))return p.textContent?.trim()||null;let B=e.parentElement;if(B){let I=B.querySelector('[role="alert"], .error-message, .field-error, .validation-message');if(I&&C(I))return I.textContent?.trim()||null}return null}function m(e){if(e.id&&c.has(`#${e.id}`))return`#${e.id}`;for(let r of c)try{if(e.matches(r))return r}catch{}return null}function k(e,r){let a=e.parentElement;if(a)for(let p of c)try{a.querySelector(p)&&r.add(p)}catch{}}function C(e){return e instanceof HTMLElement?e.offsetParent!==null&&!e.hidden:!0}function L(){y!==null&&(clearTimeout(y),y=null)}function _(e){"requestIdleCallback"in window?requestIdleCallback(e,{timeout:100}):setTimeout(e,16)}function P(){document.addEventListener("focusin",x,{passive:!0}),document.addEventListener("focusout",E,{passive:!0}),document.addEventListener("submit",S,{passive:!0,capture:!0}),M.observe(document.body,{subtree:!0,childList:!0,attributes:!0,attributeFilter:["class","aria-invalid","aria-errormessage","aria-describedby","disabled","hidden","aria-hidden"],characterData:!1});for(let e of c)try{let r=document.querySelector(e);r&&(T.set(r,e),g.observe(r),d.set(e,!0))}catch{}for(let e of c){let r=document.querySelector(e);r&&v(e,r)}}function W(){document.removeEventListener("focusin",x),document.removeEventListener("focusout",E),document.removeEventListener("submit",S,{capture:!0}),M.disconnect(),g.disconnect(),L(),T.clear(),u.clear(),d.clear()}function U(){let e={};for(let[r,a]of u)e[r]=a;return e}function V(){return Array.from(d.entries()).filter(([,e])=>e).map(([e])=>e)}return{start:P,destroy:W,getFieldStates:U,getVisibleFieldIds:V}}function fe(t,n){return t.when.split(/\s+AND\s+/i).every(s=>ge(s.trim(),n))}function K(t,n){return t.filter(o=>fe(o,n))}function ge(t,n){let o=t.match(/^\[([^\]]+)\]\.error(?:\.(\w+))?$/);if(o){let s=n.fieldStates[o[1]];return!s||!s.hasError?!1:o[2]&&s.errorText?s.errorText.toLowerCase().includes(o[2].toLowerCase()):s.hasError}if(o=t.match(/^\[([^\]]+)\]\.empty$/),o){let s=n.fieldStates[o[1]];return s?!s.hasValue:!0}return o=t.match(/^\[([^\]]+)\]\.changed$/),o?n.changedFields.has(o[1]):(o=t.match(/^\[([^\]]+)\]\.focus$/),o?n.fieldStates[o[1]]?.hasFocus??!1:(o=t.match(/^page\.idle\s*>\s*(\d+)s$/),o?n.idleSeconds>=parseInt(o[1],10):t==="form.incomplete"?n.hasIncompleteRequired:(o=t.match(/^user\.attempts\s*>\s*(\d+)$/),o?n.submitAttempts>parseInt(o[1],10):!1)))}var me={rage_click:3,exit_intent:2.5,validation_error:2,same_field_error:2.5,slow_dwell:1.5,field_cycling:1.5,repeated_correction:1,dead_click:1},he=2*60*1e3;function Q(t){let n=[],o=0,s=0,l=t.threshold,i=new Map,c=null,u=0,d=[],f=new Map,y=new Set,h=[];function x(v){let w=Date.now();n.push({signal:v,weight:me[v],ts:w}),g(w),E()>=l&&M(w)&&t.onFrustrated()}function E(){let v=Date.now();return g(v),n.reduce((w,F)=>w+F.weight,0)}function S(){n.length=0}function M(v){if(s===0)return!0;let w=[6e4,12e4,24e4],F=w[Math.min(o-1,w.length-1)]??6e4;return v-s>=F}function b(){o++,s=Date.now(),o>=3&&(l=7.5),S()}function g(v){let w=v-he;for(;n.length>0&&n[0].ts<w;)n.shift()}function T(){function v(A){let m=A.target;if(!(m instanceof Element))return;let k=m.id||m.className.toString().slice(0,50);if(!k)return;let C=Date.now(),L=i.get(k)??[];L.push(C);let _=L.filter(P=>C-P<1e3);i.set(k,_),_.length>=3&&(x("rage_click"),i.set(k,[])),m instanceof HTMLInputElement||m instanceof HTMLButtonElement||m instanceof HTMLAnchorElement||m instanceof HTMLSelectElement||m instanceof HTMLTextAreaElement||m.getAttribute("role")==="button"||setTimeout(()=>{x("dead_click")},2e3)}function w(A){A.clientY<=0&&x("exit_intent")}function F(A){let m=A.target;if(!(m instanceof HTMLInputElement||m instanceof HTMLTextAreaElement||m instanceof HTMLSelectElement))return;let k=m.id?`#${m.id}`:m.name||"",C=Date.now();c&&C-u>3e4&&x("slow_dwell"),c=k,u=C,d.push(C),d=d.filter(L=>C-L<1e4),d.length>=5&&(x("field_cycling"),d=[])}function z(A){let m=A.target;if(!(m instanceof HTMLInputElement||m instanceof HTMLTextAreaElement))return;let k=m.id?`#${m.id}`:m.name||"";if(!k)return;let C=Date.now();if(m.value.length===0){let L=f.get(k)??[];L.push(C);let _=L.filter(P=>C-P<3e4);f.set(k,_),_.length>=3&&(x("repeated_correction"),f.set(k,[]))}}document.addEventListener("click",v,{passive:!0}),document.documentElement.addEventListener("mouseleave",w,{passive:!0}),document.addEventListener("focusin",F,{passive:!0}),document.addEventListener("input",z,{passive:!0}),h.push(()=>document.removeEventListener("click",v),()=>document.documentElement.removeEventListener("mouseleave",w),()=>document.removeEventListener("focusin",F),()=>document.removeEventListener("input",z))}return T(),{recordSignal:x,getScore:E,reset:S,recordValidationError(v){y.has(v)?x("same_field_error"):(y.add(v),x("validation_error"))},onDismiss:b,destroy(){for(let v of h)v();h.length=0,n.length=0}}}var Z=`
/* \u2500\u2500 Reset & Host \u2500\u2500 */
:host {
  all: initial;
  position: fixed;
  z-index: 2147483647;
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif;
  font-size: 14px;
  line-height: 1.5;
  color-scheme: light dark;
}

*, *::before, *::after {
  box-sizing: border-box;
}

/* \u2500\u2500 Theme Variables \u2500\u2500 */
.ls-root[data-theme="light"] {
  --ls-bg: #ffffff;
  --ls-bg-secondary: #f8f9fa;
  --ls-text: #1a1a2e;
  --ls-text-secondary: #6b7280;
  --ls-border: #e5e7eb;
  --ls-primary: #3b82f6;
  --ls-primary-hover: #2563eb;
  --ls-primary-text: #ffffff;
  --ls-error: #ef4444;
  --ls-success: #22c55e;
  --ls-shadow: 0 4px 24px rgba(0, 0, 0, 0.12);
  --ls-shadow-sm: 0 2px 8px rgba(0, 0, 0, 0.08);
  --ls-toast-bg: #1a1a2e;
  --ls-toast-text: #f8f9fa;
}

.ls-root[data-theme="dark"] {
  --ls-bg: #1e1e2e;
  --ls-bg-secondary: #2a2a3e;
  --ls-text: #e5e7eb;
  --ls-text-secondary: #9ca3af;
  --ls-border: #374151;
  --ls-primary: #60a5fa;
  --ls-primary-hover: #93bbfd;
  --ls-primary-text: #1a1a2e;
  --ls-error: #f87171;
  --ls-success: #4ade80;
  --ls-shadow: 0 4px 24px rgba(0, 0, 0, 0.4);
  --ls-shadow-sm: 0 2px 8px rgba(0, 0, 0, 0.3);
  --ls-toast-bg: #f8f9fa;
  --ls-toast-text: #1a1a2e;
}

/* \u2500\u2500 FAB (Floating Action Button) \u2500\u2500 */
.ls-fab {
  position: fixed;
  width: 48px;
  height: 48px;
  border-radius: 50%;
  border: none;
  background: var(--ls-primary);
  color: var(--ls-primary-text);
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
  box-shadow: var(--ls-shadow);
  transition: transform 0.2s cubic-bezier(0.4, 0, 0.2, 1),
              box-shadow 0.2s cubic-bezier(0.4, 0, 0.2, 1);
  z-index: 1;
}

.ls-fab:hover {
  transform: scale(1.08);
  box-shadow: var(--ls-shadow), 0 0 0 4px rgba(59, 130, 246, 0.2);
}

.ls-fab:active {
  transform: scale(0.95);
}

.ls-fab svg {
  width: 22px;
  height: 22px;
  fill: currentColor;
}

.ls-fab-hidden {
  transform: scale(0);
  pointer-events: none;
}

/* Position variants */
.ls-root[data-position="bottom-right"] .ls-fab {
  bottom: 20px;
  right: 20px;
}

.ls-root[data-position="bottom-left"] .ls-fab {
  bottom: 20px;
  left: 20px;
}

/* \u2500\u2500 Toast \u2500\u2500 */
.ls-toast-container {
  position: fixed;
  display: flex;
  flex-direction: column;
  gap: 8px;
  z-index: 2;
  max-width: 360px;
}

.ls-root[data-position="bottom-right"] .ls-toast-container {
  bottom: 80px;
  right: 20px;
}

.ls-root[data-position="bottom-left"] .ls-toast-container {
  bottom: 80px;
  left: 20px;
}

.ls-toast {
  background: var(--ls-toast-bg);
  color: var(--ls-toast-text);
  padding: 12px 16px;
  border-radius: 12px;
  box-shadow: var(--ls-shadow);
  transform: translateX(120%);
  transition: transform 0.25s cubic-bezier(0.4, 0, 0.2, 1),
              opacity 0.25s cubic-bezier(0.4, 0, 0.2, 1);
  opacity: 0;
  cursor: pointer;
  display: flex;
  align-items: flex-start;
  gap: 10px;
  max-width: 100%;
}

.ls-root[data-position="bottom-left"] .ls-toast {
  transform: translateX(-120%);
}

.ls-toast-show {
  transform: translateX(0);
  opacity: 1;
}

.ls-toast-text {
  flex: 1;
  font-size: 13px;
  line-height: 1.4;
}

.ls-toast-dismiss {
  flex-shrink: 0;
  background: none;
  border: none;
  color: inherit;
  opacity: 0.6;
  cursor: pointer;
  padding: 0;
  font-size: 16px;
  line-height: 1;
}

.ls-toast-dismiss:hover {
  opacity: 1;
}

/* \u2500\u2500 Panel \u2500\u2500 */
.ls-panel {
  position: fixed;
  background: var(--ls-bg);
  box-shadow: var(--ls-shadow);
  display: flex;
  flex-direction: column;
  transform: translateX(100%);
  transition: transform 0.3s cubic-bezier(0.4, 0, 0.2, 1);
  z-index: 3;
  overflow: hidden;
}

.ls-root[data-position="bottom-right"] .ls-panel {
  top: 0;
  right: 0;
  width: 380px;
  max-width: 100vw;
  height: 100vh;
  height: 100dvh;
  border-left: 1px solid var(--ls-border);
}

.ls-root[data-position="bottom-left"] .ls-panel {
  top: 0;
  left: 0;
  width: 380px;
  max-width: 100vw;
  height: 100vh;
  height: 100dvh;
  border-right: 1px solid var(--ls-border);
  transform: translateX(-100%);
}

.ls-panel-open {
  transform: translateX(0);
}

.ls-panel-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 14px 16px;
  border-bottom: 1px solid var(--ls-border);
  background: var(--ls-bg);
  flex-shrink: 0;
}

.ls-panel-title {
  font-weight: 600;
  font-size: 15px;
  color: var(--ls-text);
}

.ls-panel-close {
  background: none;
  border: none;
  cursor: pointer;
  color: var(--ls-text-secondary);
  padding: 4px;
  border-radius: 4px;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: background 0.15s;
}

.ls-panel-close:hover {
  background: var(--ls-bg-secondary);
  color: var(--ls-text);
}

.ls-panel-close svg {
  width: 18px;
  height: 18px;
  fill: currentColor;
}

.ls-panel-body {
  flex: 1;
  overflow-y: auto;
  padding: 16px;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.ls-panel-body::-webkit-scrollbar {
  width: 4px;
}

.ls-panel-body::-webkit-scrollbar-thumb {
  background: var(--ls-border);
  border-radius: 2px;
}

/* \u2500\u2500 Response Bubble \u2500\u2500 */
.ls-response {
  background: var(--ls-bg-secondary);
  border-radius: 12px;
  padding: 12px 14px;
  font-size: 13px;
  color: var(--ls-text);
  line-height: 1.5;
}

.ls-response:empty {
  display: none;
}

/* \u2500\u2500 Welcome/Context \u2500\u2500 */
.ls-context {
  font-size: 13px;
  color: var(--ls-text-secondary);
  line-height: 1.4;
}

/* \u2500\u2500 Suggestions \u2500\u2500 */
.ls-suggestions {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.ls-suggestions:empty {
  display: none;
}

.ls-chip {
  background: var(--ls-bg-secondary);
  border: 1px solid var(--ls-border);
  border-radius: 16px;
  padding: 5px 12px;
  font-size: 12px;
  color: var(--ls-primary);
  cursor: pointer;
  transition: background 0.15s, border-color 0.15s;
  white-space: nowrap;
}

.ls-chip:hover {
  background: var(--ls-primary);
  color: var(--ls-primary-text);
  border-color: var(--ls-primary);
}

/* \u2500\u2500 Topic Links \u2500\u2500 */
.ls-topics {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.ls-topics:empty {
  display: none;
}

.ls-topic-link {
  font-size: 13px;
  color: var(--ls-primary);
  text-decoration: none;
  padding: 4px 0;
  cursor: pointer;
}

.ls-topic-link:hover {
  text-decoration: underline;
}

/* \u2500\u2500 Input Area \u2500\u2500 */
.ls-panel-input {
  display: flex;
  gap: 8px;
  padding: 12px 16px;
  border-top: 1px solid var(--ls-border);
  background: var(--ls-bg);
  flex-shrink: 0;
}

.ls-input {
  flex: 1;
  border: 1px solid var(--ls-border);
  border-radius: 20px;
  padding: 8px 14px;
  font-size: 13px;
  background: var(--ls-bg-secondary);
  color: var(--ls-text);
  outline: none;
  transition: border-color 0.15s;
}

.ls-input::placeholder {
  color: var(--ls-text-secondary);
}

.ls-input:focus {
  border-color: var(--ls-primary);
}

.ls-send {
  width: 36px;
  height: 36px;
  border-radius: 50%;
  border: none;
  background: var(--ls-primary);
  color: var(--ls-primary-text);
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: background 0.15s;
  flex-shrink: 0;
}

.ls-send:hover {
  background: var(--ls-primary-hover);
}

.ls-send:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.ls-send svg {
  width: 16px;
  height: 16px;
  fill: currentColor;
}

/* \u2500\u2500 Loading Spinner \u2500\u2500 */
.ls-spinner {
  display: inline-flex;
  gap: 4px;
  padding: 8px 0;
}

.ls-spinner-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--ls-primary);
  animation: ls-bounce 1.2s infinite ease-in-out;
}

.ls-spinner-dot:nth-child(2) { animation-delay: 0.16s; }
.ls-spinner-dot:nth-child(3) { animation-delay: 0.32s; }

@keyframes ls-bounce {
  0%, 80%, 100% { transform: scale(0.6); opacity: 0.4; }
  40% { transform: scale(1); opacity: 1; }
}

/* \u2500\u2500 Highlights (overlay on host page elements) \u2500\u2500 */
.ls-highlight {
  position: fixed;
  border-radius: 4px;
  pointer-events: none;
  transition: opacity 0.3s, box-shadow 0.3s;
  opacity: 0;
  z-index: 2147483646;
}

.ls-highlight-active {
  opacity: 1;
}

.ls-highlight-error {
  box-shadow: 0 0 0 3px rgba(239, 68, 68, 0.5), 0 0 12px rgba(239, 68, 68, 0.2);
}

.ls-highlight-info {
  box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.5), 0 0 12px rgba(59, 130, 246, 0.2);
}

.ls-highlight-success {
  box-shadow: 0 0 0 3px rgba(34, 197, 94, 0.5), 0 0 12px rgba(34, 197, 94, 0.2);
}

.ls-highlight-fade {
  opacity: 0;
}

/* Pulsing animation for active highlights */
.ls-highlight-active.ls-highlight-error {
  animation: ls-pulse-error 2s ease-in-out infinite;
}

.ls-highlight-active.ls-highlight-info {
  animation: ls-pulse-info 2s ease-in-out infinite;
}

@keyframes ls-pulse-error {
  0%, 100% { box-shadow: 0 0 0 3px rgba(239, 68, 68, 0.5), 0 0 12px rgba(239, 68, 68, 0.2); }
  50% { box-shadow: 0 0 0 5px rgba(239, 68, 68, 0.3), 0 0 20px rgba(239, 68, 68, 0.15); }
}

@keyframes ls-pulse-info {
  0%, 100% { box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.5), 0 0 12px rgba(59, 130, 246, 0.2); }
  50% { box-shadow: 0 0 0 5px rgba(59, 130, 246, 0.3), 0 0 20px rgba(59, 130, 246, 0.15); }
}

/* \u2500\u2500 Mobile \u2500\u2500 */
@media (max-width: 767px) {
  .ls-panel {
    top: auto;
    bottom: 0;
    left: 0;
    right: 0;
    width: 100%;
    max-height: 70vh;
    max-height: 70dvh;
    border-radius: 16px 16px 0 0;
    border-left: none;
    border-right: none;
    border-top: 1px solid var(--ls-border);
    transform: translateY(100%);
  }

  .ls-root[data-position="bottom-left"] .ls-panel {
    transform: translateY(100%);
  }

  .ls-panel-open {
    transform: translateY(0);
  }

  .ls-toast-container {
    left: 12px;
    right: 12px;
    max-width: none;
  }

  .ls-toast {
    transform: translateY(120%);
  }

  .ls-toast-show {
    transform: translateY(0);
  }
}

/* \u2500\u2500 Struggling Mode Toast \u2500\u2500 */
.ls-toast-struggling {
  flex-direction: column;
  gap: 8px;
}

.ls-struggling-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.ls-struggling-guide {
  background: var(--ls-primary);
  color: var(--ls-primary-text);
  border: none;
  border-radius: 16px;
  padding: 6px 14px;
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
  transition: background 0.15s;
  white-space: nowrap;
}

.ls-struggling-guide:hover {
  background: var(--ls-primary-hover);
}

/* Pulsing FAB when struggling */
.ls-fab-pulse {
  animation: ls-fab-pulse 2s ease-in-out infinite;
}

@keyframes ls-fab-pulse {
  0%, 100% { box-shadow: var(--ls-shadow); }
  50% { box-shadow: var(--ls-shadow), 0 0 0 8px rgba(59, 130, 246, 0.2); }
}

/* \u2500\u2500 Active/Guide Mode \u2500\u2500 */
.ls-guide-progress {
  padding: 8px 0;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.ls-progress-bar {
  height: 4px;
  background: var(--ls-primary);
  border-radius: 2px;
  transition: width 0.3s ease-out;
}

.ls-guide-progress::before {
  content: '';
  display: block;
  height: 4px;
  background: var(--ls-border);
  border-radius: 2px;
  position: absolute;
  left: 0;
  right: 0;
}

.ls-guide-progress {
  position: relative;
}

.ls-progress-text {
  font-size: 11px;
  color: var(--ls-text-secondary);
  text-align: center;
}

.ls-guide-current {
  background: var(--ls-bg-secondary);
  border-radius: 8px;
  padding: 12px;
  display: flex;
  flex-direction: column;
  gap: 6px;
  border-left: 3px solid var(--ls-primary);
}

.ls-guide-field-label {
  font-weight: 600;
  font-size: 14px;
  color: var(--ls-text);
}

.ls-guide-help {
  font-size: 13px;
  color: var(--ls-text-secondary);
  line-height: 1.4;
}

.ls-guide-format {
  font-size: 12px;
  color: var(--ls-primary);
  font-family: 'SF Mono', 'Fira Code', 'Consolas', monospace;
}

.ls-guide-next {
  font-size: 12px;
  color: var(--ls-text-secondary);
  padding: 4px 0;
  font-style: italic;
}

.ls-guide-exit {
  margin-top: auto;
  padding: 8px 16px;
  background: transparent;
  border: 1px solid var(--ls-border);
  border-radius: var(--ls-radius, 6px);
  color: var(--ls-text-secondary);
  font-size: 12px;
  cursor: pointer;
  transition: background 0.15s, color 0.15s;
  align-self: center;
}

.ls-guide-exit:hover {
  background: var(--ls-bg-secondary);
  color: var(--ls-text);
}

/* \u2500\u2500 Focus visible outlines \u2500\u2500 */
.ls-fab:focus-visible,
.ls-panel-close:focus-visible,
.ls-chip:focus-visible,
.ls-send:focus-visible,
.ls-input:focus-visible {
  outline: 2px solid var(--ls-primary);
  outline-offset: 2px;
}

/* \u2500\u2500 Reduced motion \u2500\u2500 */
@media (prefers-reduced-motion: reduce) {
  *, *::before, *::after {
    animation-duration: 0.01ms !important;
    transition-duration: 0.01ms !important;
  }
}
`;var be='<svg viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg"><path d="M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 17h-2v-2h2v2zm2.07-7.75l-.9.92C13.45 12.9 13 13.5 13 15h-2v-.5c0-1.1.45-2.1 1.17-2.83l1.24-1.26c.37-.36.59-.86.59-1.41 0-1.1-.9-2-2-2s-2 .9-2 2H8c0-2.21 1.79-4 4-4s4 1.79 4 4c0 .88-.36 1.68-.93 2.25z"/></svg>',ve='<svg viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg"><path d="M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z"/></svg>',xe='<svg viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg"><path d="M2.01 21L23 12 2.01 3 2 10l15 2-15 2z"/></svg>';function te(t,n,o,s){let l=document.createElement("style");l.textContent=Z,t.appendChild(l);let i=document.createElement("div");i.className="ls-root",i.setAttribute("data-theme",o),i.setAttribute("data-position",n);let c=document.createElement("button");c.className="ls-fab",c.setAttribute("aria-label","Open help"),c.innerHTML=be,c.addEventListener("click",s.onFabClick);let u=document.createElement("div");u.className="ls-toast-container",u.setAttribute("role","status"),u.setAttribute("aria-live","polite");let d=document.createElement("div");d.className="ls-panel",d.setAttribute("role","dialog"),d.setAttribute("aria-label","Help panel");let f=document.createElement("div");f.className="ls-panel-header";let y=document.createElement("span");y.className="ls-panel-title",y.textContent="Help";let h=document.createElement("button");h.className="ls-panel-close",h.setAttribute("aria-label","Close help panel"),h.innerHTML=ve,h.addEventListener("click",s.onPanelClose),f.appendChild(y),f.appendChild(h);let x=document.createElement("div");x.className="ls-panel-body";let E=document.createElement("div");E.className="ls-response";let S=document.createElement("div");S.className="ls-suggestions";let M=document.createElement("div");M.className="ls-topics",x.appendChild(E),x.appendChild(S),x.appendChild(M);let b=document.createElement("div");b.className="ls-panel-input";let g=document.createElement("input");g.type="text",g.className="ls-input",g.placeholder="Ask a question...",g.setAttribute("aria-label","Ask a help question");let T=document.createElement("button");T.className="ls-send",T.setAttribute("aria-label","Send"),T.innerHTML=xe;function v(){let w=g.value.trim();w&&(s.onSendMessage(w),g.value="")}return T.addEventListener("click",v),g.addEventListener("keydown",w=>{w.key==="Enter"&&!w.shiftKey&&(w.preventDefault(),v())}),b.appendChild(g),b.appendChild(T),d.appendChild(f),d.appendChild(x),d.appendChild(b),i.appendChild(c),i.appendChild(u),i.appendChild(d),t.appendChild(i),{root:i,fab:c,toastContainer:u,panel:d,panelBody:x,responseArea:E,suggestionsArea:S,topicsArea:M,input:g,sendBtn:T}}var N=new Map;function ne(t,n,o){let s=document.createElement("div");s.className="ls-toast",s.setAttribute("role","status");let l=document.createElement("span");l.className="ls-toast-text",l.textContent=n.suggest;let i=document.createElement("button");i.className="ls-toast-dismiss",i.setAttribute("aria-label","Dismiss"),i.textContent="\xD7",s.appendChild(l),s.appendChild(i),l.addEventListener("click",()=>{G(s),o(n)}),i.addEventListener("click",u=>{u.stopPropagation(),G(s)}),t.appendChild(s),requestAnimationFrame(()=>{requestAnimationFrame(()=>s.classList.add("ls-toast-show"))});let c=setTimeout(()=>G(s),1e4);return N.set(s,c),s}function G(t){let n=N.get(t);n&&clearTimeout(n),N.delete(t),t.classList.remove("ls-toast-show"),t.addEventListener("transitionend",()=>t.remove(),{once:!0}),setTimeout(()=>t.remove(),400)}function D(t){for(let[n,o]of N)clearTimeout(o),n.remove();N.clear()}function Y(t,n){t.classList.add("ls-panel-open"),n.classList.add("ls-fab-hidden")}function oe(t,n){t.classList.remove("ls-panel-open"),n.classList.remove("ls-fab-hidden")}function se(t,n,o){t.responseArea.textContent=n.text,t.suggestionsArea.textContent="";for(let s of n.suggestions){let l=document.createElement("button");l.className="ls-chip",l.textContent=s,l.addEventListener("click",()=>o.onChipClick(s)),t.suggestionsArea.appendChild(l)}t.topicsArea.textContent="";for(let s of n.topics){let l=document.createElement("a");l.className="ls-topic-link",l.textContent=s.label,l.href="#",l.addEventListener("click",i=>{i.preventDefault(),o.onTopicClick(s.id)}),t.topicsArea.appendChild(l)}}function re(t){t.responseArea.textContent="";let n=document.createElement("div");n.className="ls-spinner";for(let o=0;o<3;o++){let s=document.createElement("div");s.className="ls-spinner-dot",n.appendChild(s)}t.responseArea.appendChild(n)}function ie(t,n){t.responseArea.textContent=n}function ae(t,n){let o=document.createElement("div");o.className="ls-context",o.textContent=`I can help you with the ${n} page. Ask a question or click a suggestion below.`,t.panelBody.insertBefore(o,t.responseArea)}var H=new Map,R=null;function q(t,n,o){let s=document.querySelector(n);if(!s)return;let l=s.getBoundingClientRect(),i=document.createElement("div");i.className=`ls-highlight ls-highlight-${o}`,i.style.position="fixed",i.style.top=`${l.top-3}px`,i.style.left=`${l.left-3}px`,i.style.width=`${l.width+6}px`,i.style.height=`${l.height+6}px`,i.style.pointerEvents="none";let c=t.querySelector(".ls-root");c&&c.appendChild(i),requestAnimationFrame(()=>i.classList.add("ls-highlight-active")),H.set(i,n),H.size===1&&ye(),setTimeout(()=>{i.classList.remove("ls-highlight-active"),i.classList.add("ls-highlight-fade"),i.addEventListener("transitionend",()=>{i.remove(),H.delete(i),H.size===0&&le()},{once:!0}),setTimeout(()=>{i.remove(),H.delete(i)},400)},5e3)}function O(){for(let[t]of H)t.remove();H.clear(),le()}function ye(){function t(){for(let[n,o]of H){let s=document.querySelector(o);if(!s){n.remove(),H.delete(n);continue}let l=s.getBoundingClientRect();n.style.top=`${l.top-3}px`,n.style.left=`${l.left-3}px`,n.style.width=`${l.width+6}px`,n.style.height=`${l.height+6}px`}H.size>0&&(R=requestAnimationFrame(t))}R=requestAnimationFrame(t)}function le(){R!==null&&(cancelAnimationFrame(R),R=null)}function ce(t,n){let o=document.createElement("div");o.className="ls-toast ls-toast-struggling",o.setAttribute("role","status");let s=document.createElement("div");s.className="ls-toast-text",s.textContent="Need help with this form?";let l=document.createElement("div");l.className="ls-struggling-actions";let i=document.createElement("button");i.className="ls-struggling-guide",i.textContent="Guide me through it",i.addEventListener("click",d=>{d.stopPropagation(),j(o),n.onAcceptGuide()});let c=document.createElement("button");c.className="ls-toast-dismiss",c.setAttribute("aria-label","Dismiss"),c.textContent="\xD7",c.addEventListener("click",d=>{d.stopPropagation(),j(o),n.onDismissStruggling()}),l.appendChild(i),l.appendChild(c),o.appendChild(s),o.appendChild(l),t.appendChild(o),requestAnimationFrame(()=>{requestAnimationFrame(()=>o.classList.add("ls-toast-show"))});let u=setTimeout(()=>{j(o),n.onDismissStruggling()},15e3);return N.set(o,u),o}function j(t){let n=N.get(t);n&&clearTimeout(n),N.delete(t),t.classList.remove("ls-toast-show"),t.addEventListener("transitionend",()=>t.remove(),{once:!0}),setTimeout(()=>t.remove(),400)}var ee=null;function de(t,n,o,s,l){l&&(ee=l),t.panelBody.textContent="";let i=document.createElement("div");i.className="ls-guide-progress";let c=document.createElement("div");c.className="ls-progress-bar";let u=n.fields.length>0?Math.round(s.size/n.fields.length*100):0;c.style.width=`${u}%`;let d=document.createElement("span");if(d.className="ls-progress-text",d.textContent=`${s.size} of ${n.fields.length} fields complete`,i.appendChild(c),i.appendChild(d),t.panelBody.appendChild(i),o>=0&&o<n.fields.length){let y=n.fields[o],h=document.createElement("div");h.className="ls-guide-current",h.setAttribute("aria-live","polite");let x=document.createElement("div");if(x.className="ls-guide-field-label",x.textContent=y.label,h.appendChild(x),y.help){let E=document.createElement("div");E.className="ls-guide-help",E.textContent=y.help,h.appendChild(E)}if(y.pattern){let E=document.createElement("div");E.className="ls-guide-format",E.textContent=`Format: ${y.pattern}`,h.appendChild(E)}if(t.panelBody.appendChild(h),o+1<n.fields.length){let E=n.fields[o+1],S=document.createElement("div");S.className="ls-guide-next",S.textContent=`Next: ${E.label}`,t.panelBody.appendChild(S)}}t.panelBody.appendChild(t.responseArea),t.panelBody.appendChild(t.suggestionsArea),t.panelBody.appendChild(t.topicsArea);let f=document.createElement("button");f.className="ls-guide-exit",f.textContent="Exit guided mode",f.addEventListener("click",()=>{ee?.()}),t.panelBody.appendChild(f)}function ue(t,n,o,s){let l=t.panelBody.querySelector(".ls-progress-bar"),i=t.panelBody.querySelector(".ls-progress-text");if(l&&i){let d=n.fields.length>0?Math.round(s.size/n.fields.length*100):0;l.style.width=`${d}%`,i.textContent=`${s.size} of ${n.fields.length} fields complete`}let c=t.panelBody.querySelector(".ls-guide-current");if(c&&o>=0&&o<n.fields.length){let d=n.fields[o],f=c.querySelector(".ls-guide-field-label");f&&(f.textContent=d.label);let y=c.querySelector(".ls-guide-help");y&&(y.textContent=d.help??"");let h=c.querySelector(".ls-guide-format");h&&(h.textContent=d.pattern?`Format: ${d.pattern}`:"")}let u=t.panelBody.querySelector(".ls-guide-next");u&&(o+1<n.fields.length?u.textContent=`Next: ${n.fields[o+1].label}`:u.textContent="All fields covered!")}function pe(){if(window.matchMedia("(prefers-color-scheme: dark)").matches)return"dark";let t=getComputedStyle(document.body).backgroundColor,n=Ee(t);return n&&we(n)<.5?"dark":"light"}function Ee(t){let n=t.match(/rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)/);return n?[parseInt(n[1]),parseInt(n[2]),parseInt(n[3])]:null}function we([t,n,o]){return(.2126*t+.7152*n+.0722*o)/255}var Ce=[{from:"idle",event:"condition_trigger",to:"toast"},{from:"idle",event:"fab_click",to:"panel"},{from:"idle",event:"field_idle",to:"toast"},{from:"toast",event:"toast_click",to:"panel"},{from:"toast",event:"toast_dismiss",to:"idle"},{from:"toast",event:"toast_timeout",to:"idle"},{from:"panel",event:"ask_question",to:"asking"},{from:"panel",event:"panel_close",to:"idle"},{from:"asking",event:"response",to:"showing"},{from:"asking",event:"error",to:"panel"},{from:"showing",event:"panel_close",to:"idle"},{from:"showing",event:"ask_question",to:"asking"},{from:"idle",event:"frustration_threshold",to:"struggling"},{from:"struggling",event:"accept_guide",to:"active"},{from:"struggling",event:"dismiss_struggling",to:"idle"},{from:"struggling",event:"fab_click",to:"panel"},{from:"active",event:"exit_guide",to:"idle"},{from:"active",event:"ask_question",to:"asking"},{from:"showing",event:"exit_guide",to:"idle"}];function ke(t,n){let o=Ce.find(s=>s.from===t&&s.event===n);return o?o.to:null}function ze(t,n){let o="idle",s=null,l=null,i=null,c=null,u={fieldStates:{},changedFields:new Set,idleSeconds:0,hasIncompleteRequired:!1,submitAttempts:0},d=-1,f=new Set,y=null,h=Date.now(),x=new Set,E=X(n.api),S=n.theme==="auto"?pe():n.theme,M={onFabClick:()=>g("fab_click"),onToastClick:e=>{i=e,g("toast_click")},onPanelClose:()=>{g(o==="active"?"exit_guide":"panel_close")},onSendMessage:e=>v(e),onChipClick:e=>v(e),onTopicClick:e=>w(e),onAcceptGuide:()=>g("accept_guide"),onDismissStruggling:()=>{c?.onDismiss(),g("dismiss_struggling")},onExitGuide:()=>g("exit_guide")},b=te(t,n.position,S,M);function g(e){let r=ke(o,e);if(r===null)return;let a=o;o=r,T(a,r,e)}function T(e,r,a){switch(r){case"idle":oe(b.panel,b.fab),D(b.toastContainer),O(),f.clear(),d=-1;break;case"toast":i&&ne(b.toastContainer,i,p=>{i=p,g("toast_click")});break;case"panel":D(b.toastContainer),Y(b.panel,b.fab),s&&(ae(b,s.title),z()),e==="toast"&&i&&(b.responseArea.textContent=i.suggest,i.highlight&&q(t,i.highlight,"info")),b.input.focus();break;case"asking":re(b);break;case"showing":break;case"struggling":ce(b.toastContainer,M);break;case"active":D(b.toastContainer),Y(b.panel,b.fab),f.clear(),d=0,s&&(de(b,s,d,f,()=>g("exit_guide")),s.fields.length>0&&q(t,s.fields[0].selector,"info"));break}E.trackEvent("state_change",{from:e,to:r,event:a})}async function v(e){g("ask_question");let r=A(e);try{let a=await E.askForHelp(r);F(a),g("response")}catch{ie(b,"Sorry, help is temporarily unavailable. Please try again."),g("error")}}function w(e){let r=s?.topics.find(a=>a.articleId===e);r&&v(r.question)}function F(e){se(b,e,M);for(let r of e.highlights)q(t,r.selector,r.style)}function z(){if(s?.topics.length){b.suggestionsArea.textContent="";for(let e of s.topics){let r=document.createElement("button");r.className="ls-chip",r.textContent=e.question,r.addEventListener("click",()=>v(e.question)),b.suggestionsArea.appendChild(r)}}}function A(e){let r=l?.getFieldStates()??{},a=l?.getVisibleFieldIds()??[];return{url:location.pathname,visibleFieldIds:a,fieldStates:r,viewportWidth:window.innerWidth,question:e}}function m(){if(!s?.conditions.length||o!=="idle")return;let e=K(s.conditions,u);for(let r of e)if(!x.has(r.when)){x.add(r.when),i=r,g("condition_trigger");break}}function k(e,r){if(!(o!=="active"||!s)){if(r.hasValue&&!r.hasError?(f.add(e),q(t,e,"success")):f.delete(e),r.hasFocus){let a=s.fields.findIndex(p=>p.selector===e);if(a>=0&&a!==d){d=a,O(),q(t,e,"info");for(let p of f)q(t,p,"success")}}ue(b,s,d,f)}}function C(){h=Date.now(),u.idleSeconds=0}function L(){document.addEventListener("mousemove",C,{passive:!0}),document.addEventListener("keydown",C,{passive:!0}),document.addEventListener("scroll",C,{passive:!0}),document.addEventListener("touchstart",C,{passive:!0}),y=setInterval(()=>{u.idleSeconds=Math.floor((Date.now()-h)/1e3),m()},5e3)}function _(){document.removeEventListener("mousemove",C),document.removeEventListener("keydown",C),document.removeEventListener("scroll",C),document.removeEventListener("touchstart",C),y&&clearInterval(y)}function P(e,r){let a=u.fieldStates[e];u.fieldStates[e]=r,a&&(a.hasValue!==r.hasValue||a.hasError!==r.hasError)&&u.changedFields.add(e),r.hasError&&(!a||!a.hasError)&&c?.recordValidationError(e),s?.fields&&(u.hasIncompleteRequired=s.fields.filter(p=>p.type==="required"||p.help?.includes("required")).some(p=>{let B=u.fieldStates[p.selector];return!B||!B.hasValue})),k(e,r),m()}function W(e){if(o!=="idle")return;let r=s?.fields.find(a=>a.selector===e);r?.help&&(i={when:`[${e}].focus`,suggest:r.help,highlight:e},g("field_idle"))}function U(){u.submitAttempts++,u.changedFields.clear(),m()}async function V(){s=await E.loadPageModel(location.pathname),s&&(l=J({trackedSelectors:s.fields.map(e=>e.selector),fieldIdleMs:5e3,onFieldChange:P,onFieldIdle:W,onSubmit:U}),l.start()),c=Q({threshold:5,onFrustrated:()=>{o==="idle"&&g("frustration_threshold")}}),L(),document.addEventListener("visibilitychange",()=>{document.hidden?_():L()}),E.trackEvent("widget_loaded",{pageId:s?.pageId??"unknown",hasModel:!!s})}return V(),{destroy(){l?.destroy(),c?.destroy(),_(),O()}}}export{ze as initWidget};
//# sourceMappingURL=widget.js.map
