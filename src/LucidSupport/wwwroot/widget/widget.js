function J(e){async function t(r){try{let l=await fetch(`${e}/api/support/page?url=${encodeURIComponent(r)}`,{credentials:"omit"});return l.ok?l.json():null}catch{return null}}async function o(r){let l=await fetch(`${e}/api/help/contextual`,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(r),keepalive:!0,credentials:"omit"});if(!l.ok)throw new Error(`Help request failed: ${l.status}`);return l.json()}async function n(r,l,u){let d=await fetch(`${e}/api/help/contextual?stream=true`,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(r),credentials:"omit"});if(!d.ok)throw new Error(`Stream request failed: ${d.status}`);if(!d.body){let g=await d.text();l(g),u();return}let m=d.body.getReader(),f=new TextDecoder;try{for(;;){let{done:g,value:v}=await m.read();if(g)break;l(f.decode(v,{stream:!0}))}}finally{m.releaseLock(),u()}}function c(r,l={}){if(!navigator.sendBeacon)return;let u=JSON.stringify({event:r,...l,ts:Date.now()});navigator.sendBeacon(`${e}/api/analytics`,u)}return{loadPageModel:t,askForHelp:o,askStreaming:n,trackEvent:c}}function K(e){let{trackedSelectors:t,fieldIdleMs:o,onFieldChange:n,onFieldIdle:c,onSubmit:r}=e,l=new Set(t),u=new Map,d=new Map,m=null,f=null,g=!1;function v(a){let s=a.target;if(!(s instanceof Element))return;let i=b(s);i&&(m=i,k(),f=setTimeout(()=>c(i),o),w(i,s))}function E(a){let s=a.target;if(!(s instanceof Element))return;let i=b(s);i&&setTimeout(()=>{m===i&&(m=null),k(),w(i,s)},300)}function C(a){a.target instanceof HTMLFormElement&&r()}let I=new MutationObserver(a=>{g||(g=!0,H(()=>{M(a),g=!1}))});function M(a){let s=new Set;for(let i of a){if(i.type==="attributes"&&i.target instanceof Element){let p=b(i.target);p&&s.add(p)}if(i.type==="childList"){for(let p of i.addedNodes)p instanceof Element&&S(p,s);for(let p of i.removedNodes)p instanceof Element&&S(p,s)}}for(let i of s){let p=document.querySelector(i);p&&w(i,p)}}let h=new IntersectionObserver(a=>{for(let s of a){let i=x.get(s.target);i&&d.set(i,s.isIntersecting)}},{threshold:.1}),x=new Map;function w(a,s){let i={hasValue:A(s),hasError:W(s),errorText:_(s),hasFocus:m===a},p=u.get(a);(!p||y(p,i))&&(u.set(a,i),n(a,i))}function y(a,s){return a.hasValue!==s.hasValue||a.hasError!==s.hasError||a.errorText!==s.errorText||a.hasFocus!==s.hasFocus}function A(a){return a instanceof HTMLInputElement||a instanceof HTMLTextAreaElement?a.value.length>0:a instanceof HTMLSelectElement?a.selectedIndex>0:!1}function W(a){if(a instanceof HTMLInputElement&&!a.validity.valid||a instanceof HTMLTextAreaElement&&!a.validity.valid||a instanceof HTMLSelectElement&&!a.validity.valid||a.getAttribute("aria-invalid")==="true")return!0;let s=a.className.toString().toLowerCase();if(/\b(error|invalid|danger|has-error|is-invalid|field-error|ng-invalid)\b/.test(s))return!0;let i=a.parentElement;if(i){let p=i.className.toString().toLowerCase();if(/\b(error|invalid|has-error|field-error)\b/.test(p))return!0}return!1}function _(a){let s=a.getAttribute("aria-errormessage");if(s){let F=document.getElementById(s);if(F&&T(F))return F.textContent?.trim()||null}let i=a.getAttribute("aria-describedby");if(i)for(let F of i.split(/\s+/)){let z=document.getElementById(F);if(z&&T(z)&&/error|invalid|help/.test(z.className.toLowerCase()))return z.textContent?.trim()||null}let p=a.nextElementSibling;if(p&&T(p)&&/error|invalid|validation|field-error/.test(p.className.toLowerCase()))return p.textContent?.trim()||null;let L=a.parentElement;if(L){let F=L.querySelector('[role="alert"], .error-message, .field-error, .validation-message');if(F&&T(F))return F.textContent?.trim()||null}return null}function b(a){if(a.id&&l.has(`#${a.id}`))return`#${a.id}`;for(let s of l)try{if(a.matches(s))return s}catch{}return null}function S(a,s){let i=a.parentElement;if(i)for(let p of l)try{i.querySelector(p)&&s.add(p)}catch{}}function T(a){return a instanceof HTMLElement?a.offsetParent!==null&&!a.hidden:!0}function k(){f!==null&&(clearTimeout(f),f=null)}function H(a){"requestIdleCallback"in window?requestIdleCallback(a,{timeout:100}):setTimeout(a,16)}function R(){document.addEventListener("focusin",v,{passive:!0}),document.addEventListener("focusout",E,{passive:!0}),document.addEventListener("submit",C,{passive:!0,capture:!0}),I.observe(document.body,{subtree:!0,childList:!0,attributes:!0,attributeFilter:["class","aria-invalid","aria-errormessage","aria-describedby","disabled","hidden","aria-hidden"],characterData:!1});for(let a of l)try{let s=document.querySelector(a);s&&(x.set(s,a),h.observe(s),d.set(a,!0))}catch{}for(let a of l){let s=document.querySelector(a);s&&w(a,s)}}function O(){document.removeEventListener("focusin",v),document.removeEventListener("focusout",E),document.removeEventListener("submit",C,{capture:!0}),I.disconnect(),h.disconnect(),k(),x.clear(),u.clear(),d.clear()}function V(){let a={};for(let[s,i]of u)a[s]=i;return a}function U(){return Array.from(d.entries()).filter(([,a])=>a).map(([a])=>a)}return{start:R,destroy:O,getFieldStates:V,getVisibleFieldIds:U}}function me(e,t){return e.when.split(/\s+AND\s+/i).every(n=>he(n.trim(),t))}function Q(e,t){return e.filter(o=>me(o,t))}function he(e,t){let o=e.match(/^\[([^\]]+)\]\.error(?:\.(\w+))?$/);if(o){let n=t.fieldStates[o[1]];return!n||!n.hasError?!1:o[2]&&n.errorText?n.errorText.toLowerCase().includes(o[2].toLowerCase()):n.hasError}if(o=e.match(/^\[([^\]]+)\]\.empty$/),o){let n=t.fieldStates[o[1]];return n?!n.hasValue:!0}return o=e.match(/^\[([^\]]+)\]\.changed$/),o?t.changedFields.has(o[1]):(o=e.match(/^\[([^\]]+)\]\.focus$/),o?t.fieldStates[o[1]]?.hasFocus??!1:(o=e.match(/^page\.idle\s*>\s*(\d+)s$/),o?t.idleSeconds>=parseInt(o[1],10):e==="form.incomplete"?t.hasIncompleteRequired:(o=e.match(/^user\.attempts\s*>\s*(\d+)$/),o?t.submitAttempts>parseInt(o[1],10):!1)))}var ve={rage_click:3,exit_intent:2.5,validation_error:2,same_field_error:2.5,slow_dwell:1.5,field_cycling:1.5,repeated_correction:1,dead_click:1},be=2*60*1e3;function Z(e){let t=[],o=0,n=0,c=e.threshold,r=new Map,l=null,u=0,d=[],m=new Map,f=new Set,g=[];function v(w){let y=Date.now();t.push({signal:w,weight:ve[w],ts:y}),h(y),E()>=c&&I(y)&&e.onFrustrated()}function E(){let w=Date.now();return h(w),t.reduce((y,A)=>y+A.weight,0)}function C(){t.length=0}function I(w){if(n===0)return!0;let y=[6e4,12e4,24e4],A=y[Math.min(o-1,y.length-1)]??6e4;return w-n>=A}function M(){o++,n=Date.now(),o>=3&&(c=7.5),C()}function h(w){let y=w-be;for(;t.length>0&&t[0].ts<y;)t.shift()}function x(){function w(_){let b=_.target;if(!(b instanceof Element))return;let S=b.id||b.className.toString().slice(0,50);if(!S)return;let T=Date.now(),k=r.get(S)??[];k.push(T);let H=k.filter(R=>T-R<1e3);r.set(S,H),H.length>=3&&(v("rage_click"),r.set(S,[])),b instanceof HTMLInputElement||b instanceof HTMLButtonElement||b instanceof HTMLAnchorElement||b instanceof HTMLSelectElement||b instanceof HTMLTextAreaElement||b.getAttribute("role")==="button"||setTimeout(()=>{v("dead_click")},2e3)}function y(_){_.clientY<=0&&v("exit_intent")}function A(_){let b=_.target;if(!(b instanceof HTMLInputElement||b instanceof HTMLTextAreaElement||b instanceof HTMLSelectElement))return;let S=b.id?`#${b.id}`:b.name||"",T=Date.now();l&&T-u>3e4&&v("slow_dwell"),l=S,u=T,d.push(T),d=d.filter(k=>T-k<1e4),d.length>=5&&(v("field_cycling"),d=[])}function W(_){let b=_.target;if(!(b instanceof HTMLInputElement||b instanceof HTMLTextAreaElement))return;let S=b.id?`#${b.id}`:b.name||"";if(!S)return;let T=Date.now();if(b.value.length===0){let k=m.get(S)??[];k.push(T);let H=k.filter(R=>T-R<3e4);m.set(S,H),H.length>=3&&(v("repeated_correction"),m.set(S,[]))}}document.addEventListener("click",w,{passive:!0}),document.documentElement.addEventListener("mouseleave",y,{passive:!0}),document.addEventListener("focusin",A,{passive:!0}),document.addEventListener("input",W,{passive:!0}),g.push(()=>document.removeEventListener("click",w),()=>document.documentElement.removeEventListener("mouseleave",y),()=>document.removeEventListener("focusin",A),()=>document.removeEventListener("input",W))}return x(),{recordSignal:v,getScore:E,reset:C,recordValidationError(w){f.has(w)?v("same_field_error"):(f.add(w),v("validation_error"))},onDismiss:M,destroy(){for(let w of g)w();g.length=0,t.length=0}}}function xe(e,t,o,n){let c=[],r=[...e].sort((l,u)=>l.priority-u.priority);for(let l of r){if(!G(l.when,o))continue;let m=`${t.some(f=>`[${f.id}]`===l.target||f.id===l.target)?"ls:section":"ls:field"}:${l.action}`;c.push({type:m,target:l.target,rule:l.when,pageId:n})}return c}function ye(e){for(let t of e)document.dispatchEvent(new CustomEvent("ls:workflow",{detail:t,bubbles:!0,composed:!0}))}function G(e,t){if(e.includes(" AND "))return e.split(" AND ").every(u=>G(u.trim(),t));if(e.includes(" OR "))return e.split(" OR ").some(u=>G(u.trim(),t));let o=e.trim(),n=o.match(/^\[([^\]]+)\]\.checked$/i);if(n)return t.checkedFields.has(n[1]);let c=o.match(/^\[([^\]]+)\]\.value\s*==\s*"([^"]*)"$/i);if(c)return(t.fieldValues[c[1]]??"").toLowerCase()===c[2].toLowerCase();let r=o.match(/^\[([^\]]+)\]\.empty$/i);if(r)return(t.fieldValues[r[1]]??"")==="";let l=o.match(/^\[([^\]]+)\]\.hasValue$/i);return l?(t.fieldValues[l[1]]??"")!=="":!1}function ee(e,t,o){let n=[];function c(){let l=r(t),u=xe(e,t,l,o),d=u.map(f=>`${f.type}:${f.target}`);(d.length!==n.length||d.some((f,g)=>f!==n[g]))&&(n=d,ye(u))}function r(l){let u={},d=new Set,m=new Set;for(let f of l)for(let g of f.fields)m.add(g);for(let f of e){let g=f.when.matchAll(/\[([^\]]+)\]/g);for(let v of g)m.add(v[1])}for(let f of m){let g=document.querySelector(f);g&&((g.type==="checkbox"||g.type==="radio")&&g.checked&&d.add(f),u[f]=g.value??"")}return{fieldValues:u,checkedFields:d}}return{evaluate:c,destroy(){n=[]}}}var te=`
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
`;var we='<svg viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg"><path d="M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 17h-2v-2h2v2zm2.07-7.75l-.9.92C13.45 12.9 13 13.5 13 15h-2v-.5c0-1.1.45-2.1 1.17-2.83l1.24-1.26c.37-.36.59-.86.59-1.41 0-1.1-.9-2-2-2s-2 .9-2 2H8c0-2.21 1.79-4 4-4s4 1.79 4 4c0 .88-.36 1.68-.93 2.25z"/></svg>',Ee='<svg viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg"><path d="M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z"/></svg>',ke='<svg viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg"><path d="M2.01 21L23 12 2.01 3 2 10l15 2-15 2z"/></svg>';function oe(e,t,o,n){let c=document.createElement("style");c.textContent=te,e.appendChild(c);let r=document.createElement("div");r.className="ls-root",r.setAttribute("data-theme",o),r.setAttribute("data-position",t);let l=document.createElement("button");l.className="ls-fab",l.setAttribute("aria-label","Open help"),l.innerHTML=we,l.addEventListener("click",n.onFabClick);let u=document.createElement("div");u.className="ls-toast-container",u.setAttribute("role","status"),u.setAttribute("aria-live","polite");let d=document.createElement("div");d.className="ls-panel",d.setAttribute("role","dialog"),d.setAttribute("aria-label","Help panel");let m=document.createElement("div");m.className="ls-panel-header";let f=document.createElement("span");f.className="ls-panel-title",f.textContent="Help";let g=document.createElement("button");g.className="ls-panel-close",g.setAttribute("aria-label","Close help panel"),g.innerHTML=Ee,g.addEventListener("click",n.onPanelClose),m.appendChild(f),m.appendChild(g);let v=document.createElement("div");v.className="ls-panel-body";let E=document.createElement("div");E.className="ls-response";let C=document.createElement("div");C.className="ls-suggestions";let I=document.createElement("div");I.className="ls-topics",v.appendChild(E),v.appendChild(C),v.appendChild(I);let M=document.createElement("div");M.className="ls-panel-input";let h=document.createElement("input");h.type="text",h.className="ls-input",h.placeholder="Ask a question...",h.setAttribute("aria-label","Ask a help question");let x=document.createElement("button");x.className="ls-send",x.setAttribute("aria-label","Send"),x.innerHTML=ke;function w(){let y=h.value.trim();y&&(n.onSendMessage(y),h.value="")}return x.addEventListener("click",w),h.addEventListener("keydown",y=>{y.key==="Enter"&&!y.shiftKey&&(y.preventDefault(),w())}),M.appendChild(h),M.appendChild(x),d.appendChild(m),d.appendChild(v),d.appendChild(M),r.appendChild(l),r.appendChild(u),r.appendChild(d),e.appendChild(r),{root:r,fab:l,toastContainer:u,panel:d,panelBody:v,responseArea:E,suggestionsArea:C,topicsArea:I,input:h,sendBtn:x}}var $=new Map;function se(e,t,o){let n=document.createElement("div");n.className="ls-toast",n.setAttribute("role","status");let c=document.createElement("span");c.className="ls-toast-text",c.textContent=t.suggest;let r=document.createElement("button");r.className="ls-toast-dismiss",r.setAttribute("aria-label","Dismiss"),r.textContent="\xD7",n.appendChild(c),n.appendChild(r),c.addEventListener("click",()=>{j(n),o(t)}),r.addEventListener("click",u=>{u.stopPropagation(),j(n)}),e.appendChild(n),requestAnimationFrame(()=>{requestAnimationFrame(()=>n.classList.add("ls-toast-show"))});let l=setTimeout(()=>j(n),1e4);return $.set(n,l),n}function j(e){let t=$.get(e);t&&clearTimeout(t),$.delete(e),e.classList.remove("ls-toast-show"),e.addEventListener("transitionend",()=>e.remove(),{once:!0}),setTimeout(()=>e.remove(),400)}function B(e){for(let[t,o]of $)clearTimeout(o),t.remove();$.clear()}function X(e,t){e.classList.add("ls-panel-open"),t.classList.add("ls-fab-hidden")}function re(e,t){e.classList.remove("ls-panel-open"),t.classList.remove("ls-fab-hidden")}function ie(e,t,o){e.responseArea.textContent=t.text,e.suggestionsArea.textContent="";for(let n of t.suggestions){let c=document.createElement("button");c.className="ls-chip",c.textContent=n,c.addEventListener("click",()=>o.onChipClick(n)),e.suggestionsArea.appendChild(c)}e.topicsArea.textContent="";for(let n of t.topics){let c=document.createElement("a");c.className="ls-topic-link",c.textContent=n.label,c.href="#",c.addEventListener("click",r=>{r.preventDefault(),o.onTopicClick(n.id)}),e.topicsArea.appendChild(c)}}function ae(e){e.responseArea.textContent="";let t=document.createElement("div");t.className="ls-spinner";for(let o=0;o<3;o++){let n=document.createElement("div");n.className="ls-spinner-dot",t.appendChild(n)}e.responseArea.appendChild(t)}function le(e,t){e.responseArea.textContent=t}function ce(e,t){let o=document.createElement("div");o.className="ls-context",o.textContent=`I can help you with the ${t} page. Ask a question or click a suggestion below.`,e.panelBody.insertBefore(o,e.responseArea)}var N=new Map,q=null;function P(e,t,o){let n=document.querySelector(t);if(!n)return;let c=n.getBoundingClientRect(),r=document.createElement("div");r.className=`ls-highlight ls-highlight-${o}`,r.style.position="fixed",r.style.top=`${c.top-3}px`,r.style.left=`${c.left-3}px`,r.style.width=`${c.width+6}px`,r.style.height=`${c.height+6}px`,r.style.pointerEvents="none";let l=e.querySelector(".ls-root");l&&l.appendChild(r),requestAnimationFrame(()=>r.classList.add("ls-highlight-active")),N.set(r,t),N.size===1&&Ce(),setTimeout(()=>{r.classList.remove("ls-highlight-active"),r.classList.add("ls-highlight-fade"),r.addEventListener("transitionend",()=>{r.remove(),N.delete(r),N.size===0&&de()},{once:!0}),setTimeout(()=>{r.remove(),N.delete(r)},400)},5e3)}function D(){for(let[e]of N)e.remove();N.clear(),de()}function Ce(){function e(){for(let[t,o]of N){let n=document.querySelector(o);if(!n){t.remove(),N.delete(t);continue}let c=n.getBoundingClientRect();t.style.top=`${c.top-3}px`,t.style.left=`${c.left-3}px`,t.style.width=`${c.width+6}px`,t.style.height=`${c.height+6}px`}N.size>0&&(q=requestAnimationFrame(e))}q=requestAnimationFrame(e)}function de(){q!==null&&(cancelAnimationFrame(q),q=null)}function ue(e,t){let o=document.createElement("div");o.className="ls-toast ls-toast-struggling",o.setAttribute("role","status");let n=document.createElement("div");n.className="ls-toast-text",n.textContent="Need help with this form?";let c=document.createElement("div");c.className="ls-struggling-actions";let r=document.createElement("button");r.className="ls-struggling-guide",r.textContent="Guide me through it",r.addEventListener("click",d=>{d.stopPropagation(),Y(o),t.onAcceptGuide()});let l=document.createElement("button");l.className="ls-toast-dismiss",l.setAttribute("aria-label","Dismiss"),l.textContent="\xD7",l.addEventListener("click",d=>{d.stopPropagation(),Y(o),t.onDismissStruggling()}),c.appendChild(r),c.appendChild(l),o.appendChild(n),o.appendChild(c),e.appendChild(o),requestAnimationFrame(()=>{requestAnimationFrame(()=>o.classList.add("ls-toast-show"))});let u=setTimeout(()=>{Y(o),t.onDismissStruggling()},15e3);return $.set(o,u),o}function Y(e){let t=$.get(e);t&&clearTimeout(t),$.delete(e),e.classList.remove("ls-toast-show"),e.addEventListener("transitionend",()=>e.remove(),{once:!0}),setTimeout(()=>e.remove(),400)}var ne=null;function pe(e,t,o,n,c){c&&(ne=c),e.panelBody.textContent="";let r=document.createElement("div");r.className="ls-guide-progress";let l=document.createElement("div");l.className="ls-progress-bar";let u=t.fields.length>0?Math.round(n.size/t.fields.length*100):0;l.style.width=`${u}%`;let d=document.createElement("span");if(d.className="ls-progress-text",d.textContent=`${n.size} of ${t.fields.length} fields complete`,r.appendChild(l),r.appendChild(d),e.panelBody.appendChild(r),o>=0&&o<t.fields.length){let f=t.fields[o],g=document.createElement("div");g.className="ls-guide-current",g.setAttribute("aria-live","polite");let v=document.createElement("div");if(v.className="ls-guide-field-label",v.textContent=f.label,g.appendChild(v),f.help){let E=document.createElement("div");E.className="ls-guide-help",E.textContent=f.help,g.appendChild(E)}if(f.pattern){let E=document.createElement("div");E.className="ls-guide-format",E.textContent=`Format: ${f.pattern}`,g.appendChild(E)}if(e.panelBody.appendChild(g),o+1<t.fields.length){let E=t.fields[o+1],C=document.createElement("div");C.className="ls-guide-next",C.textContent=`Next: ${E.label}`,e.panelBody.appendChild(C)}}e.panelBody.appendChild(e.responseArea),e.panelBody.appendChild(e.suggestionsArea),e.panelBody.appendChild(e.topicsArea);let m=document.createElement("button");m.className="ls-guide-exit",m.textContent="Exit guided mode",m.addEventListener("click",()=>{ne?.()}),e.panelBody.appendChild(m)}function fe(e,t,o,n){let c=e.panelBody.querySelector(".ls-progress-bar"),r=e.panelBody.querySelector(".ls-progress-text");if(c&&r){let d=t.fields.length>0?Math.round(n.size/t.fields.length*100):0;c.style.width=`${d}%`,r.textContent=`${n.size} of ${t.fields.length} fields complete`}let l=e.panelBody.querySelector(".ls-guide-current");if(l&&o>=0&&o<t.fields.length){let d=t.fields[o],m=l.querySelector(".ls-guide-field-label");m&&(m.textContent=d.label);let f=l.querySelector(".ls-guide-help");f&&(f.textContent=d.help??"");let g=l.querySelector(".ls-guide-format");g&&(g.textContent=d.pattern?`Format: ${d.pattern}`:"")}let u=e.panelBody.querySelector(".ls-guide-next");u&&(o+1<t.fields.length?u.textContent=`Next: ${t.fields[o+1].label}`:u.textContent="All fields covered!")}function ge(){if(window.matchMedia("(prefers-color-scheme: dark)").matches)return"dark";let e=getComputedStyle(document.body).backgroundColor,t=Se(e);return t&&Te(t)<.5?"dark":"light"}function Se(e){let t=e.match(/rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)/);return t?[parseInt(t[1]),parseInt(t[2]),parseInt(t[3])]:null}function Te([e,t,o]){return(.2126*e+.7152*t+.0722*o)/255}var Le=[{from:"idle",event:"condition_trigger",to:"toast"},{from:"idle",event:"fab_click",to:"panel"},{from:"idle",event:"field_idle",to:"toast"},{from:"toast",event:"toast_click",to:"panel"},{from:"toast",event:"toast_dismiss",to:"idle"},{from:"toast",event:"toast_timeout",to:"idle"},{from:"panel",event:"ask_question",to:"asking"},{from:"panel",event:"panel_close",to:"idle"},{from:"asking",event:"response",to:"showing"},{from:"asking",event:"error",to:"panel"},{from:"showing",event:"panel_close",to:"idle"},{from:"showing",event:"ask_question",to:"asking"},{from:"idle",event:"frustration_threshold",to:"struggling"},{from:"struggling",event:"accept_guide",to:"active"},{from:"struggling",event:"dismiss_struggling",to:"idle"},{from:"struggling",event:"fab_click",to:"panel"},{from:"active",event:"exit_guide",to:"idle"},{from:"active",event:"ask_question",to:"asking"},{from:"showing",event:"exit_guide",to:"idle"}];function Me(e,t){let o=Le.find(n=>n.from===e&&n.event===t);return o?o.to:null}function Oe(e,t){let o="idle",n=null,c=null,r=null,l=null,u=null,d={fieldStates:{},changedFields:new Set,idleSeconds:0,hasIncompleteRequired:!1,submitAttempts:0},m=-1,f=new Set,g=null,v=Date.now(),E=new Set,C=J(t.api),I=t.theme==="auto"?ge():t.theme,M={onFabClick:()=>x("fab_click"),onToastClick:s=>{r=s,x("toast_click")},onPanelClose:()=>{x(o==="active"?"exit_guide":"panel_close")},onSendMessage:s=>y(s),onChipClick:s=>y(s),onTopicClick:s=>A(s),onAcceptGuide:()=>x("accept_guide"),onDismissStruggling:()=>{l?.onDismiss(),x("dismiss_struggling")},onExitGuide:()=>x("exit_guide")},h=oe(e,t.position,I,M);function x(s){let i=Me(o,s);if(i===null)return;let p=o;o=i,w(p,i,s)}function w(s,i,p){switch(i){case"idle":re(h.panel,h.fab),B(h.toastContainer),D(),f.clear(),m=-1;break;case"toast":r&&se(h.toastContainer,r,L=>{r=L,x("toast_click")});break;case"panel":B(h.toastContainer),X(h.panel,h.fab),n&&(ce(h,n.title),_()),s==="toast"&&r&&(h.responseArea.textContent=r.suggest,r.highlight&&P(e,r.highlight,"info")),h.input.focus();break;case"asking":ae(h);break;case"showing":break;case"struggling":ue(h.toastContainer,M);break;case"active":B(h.toastContainer),X(h.panel,h.fab),f.clear(),m=0,n&&(pe(h,n,m,f,()=>x("exit_guide")),n.fields.length>0&&P(e,n.fields[0].selector,"info"));break}C.trackEvent("state_change",{from:s,to:i,event:p})}async function y(s){x("ask_question");let i=b(s);try{let p=await C.askForHelp(i);W(p),x("response")}catch{le(h,"Sorry, help is temporarily unavailable. Please try again."),x("error")}}function A(s){let i=n?.topics.find(p=>p.articleId===s);i&&y(i.question)}function W(s){ie(h,s,M);for(let i of s.highlights)P(e,i.selector,i.style)}function _(){if(n?.topics.length){h.suggestionsArea.textContent="";for(let s of n.topics){let i=document.createElement("button");i.className="ls-chip",i.textContent=s.question,i.addEventListener("click",()=>y(s.question)),h.suggestionsArea.appendChild(i)}}}function b(s){let i=c?.getFieldStates()??{},p=c?.getVisibleFieldIds()??[];return{url:location.pathname,visibleFieldIds:p,fieldStates:i,viewportWidth:window.innerWidth,question:s}}function S(){if(!n?.conditions.length||o!=="idle")return;let s=Q(n.conditions,d);for(let i of s)if(!E.has(i.when)){E.add(i.when),r=i,x("condition_trigger");break}}function T(s,i){if(!(o!=="active"||!n)){if(i.hasValue&&!i.hasError?(f.add(s),P(e,s,"success")):f.delete(s),i.hasFocus){let p=n.fields.findIndex(L=>L.selector===s);if(p>=0&&p!==m){m=p,D(),P(e,s,"info");for(let L of f)P(e,L,"success")}}fe(h,n,m,f)}}function k(){v=Date.now(),d.idleSeconds=0}function H(){document.addEventListener("mousemove",k,{passive:!0}),document.addEventListener("keydown",k,{passive:!0}),document.addEventListener("scroll",k,{passive:!0}),document.addEventListener("touchstart",k,{passive:!0}),g=setInterval(()=>{d.idleSeconds=Math.floor((Date.now()-v)/1e3),S()},5e3)}function R(){document.removeEventListener("mousemove",k),document.removeEventListener("keydown",k),document.removeEventListener("scroll",k),document.removeEventListener("touchstart",k),g&&clearInterval(g)}function O(s,i){let p=d.fieldStates[s];d.fieldStates[s]=i,p&&(p.hasValue!==i.hasValue||p.hasError!==i.hasError)&&d.changedFields.add(s),i.hasError&&(!p||!p.hasError)&&l?.recordValidationError(s),n?.fields&&(d.hasIncompleteRequired=n.fields.filter(L=>L.type==="required"||L.help?.includes("required")).some(L=>{let F=d.fieldStates[L.selector];return!F||!F.hasValue})),T(s,i),u?.evaluate(),S()}function V(s){if(o!=="idle")return;let i=n?.fields.find(p=>p.selector===s);i?.help&&(r={when:`[${s}].focus`,suggest:i.help,highlight:s},x("field_idle"))}function U(){d.submitAttempts++,d.changedFields.clear(),S()}async function a(){n=await C.loadPageModel(location.pathname),n&&(c=K({trackedSelectors:n.fields.map(s=>s.selector),fieldIdleMs:5e3,onFieldChange:O,onFieldIdle:V,onSubmit:U}),c.start()),n?.workflowRules?.length&&(u=ee(n.workflowRules,n.sections??[],n.pageId),u.evaluate()),l=Z({threshold:5,onFrustrated:()=>{o==="idle"&&x("frustration_threshold")}}),H(),document.addEventListener("visibilitychange",()=>{document.hidden?R():H()}),C.trackEvent("widget_loaded",{pageId:n?.pageId??"unknown",hasModel:!!n})}return a(),{destroy(){c?.destroy(),l?.destroy(),u?.destroy(),R(),D()}}}export{Oe as initWidget};
//# sourceMappingURL=widget.js.map
