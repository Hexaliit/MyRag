function re(e){async function t(s){try{let r=await fetch(`${e}/api/support/page?url=${encodeURIComponent(s)}`,{credentials:"omit"});return r.ok?r.json():null}catch{return null}}async function n(s){let r=await fetch(`${e}/api/help/contextual`,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(s),keepalive:!0,credentials:"omit"});if(!r.ok)throw new Error(`Help request failed: ${r.status}`);return r.json()}async function o(s,r,d){let l=await fetch(`${e}/api/help/contextual?stream=true`,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(s),credentials:"omit"});if(!l.ok)throw new Error(`Stream request failed: ${l.status}`);if(!l.body){let f=await l.text();r(f),d();return}let m=l.body.getReader(),u=new TextDecoder;try{for(;;){let{done:f,value:h}=await m.read();if(f)break;r(u.decode(h,{stream:!0}))}}finally{m.releaseLock(),d()}}function a(s,r={}){if(!navigator.sendBeacon)return;let d=JSON.stringify({event:s,...r,ts:Date.now()});navigator.sendBeacon(`${e}/api/analytics`,d)}return{loadPageModel:t,askForHelp:n,askStreaming:o,trackEvent:a}}function ie(e){let{trackedSelectors:t,fieldIdleMs:n,onFieldChange:o,onFieldIdle:a,onSubmit:s}=e,r=new Set(t),d=new Map,l=new Map,m=null,u=null,f=!1;function h(i){let g=i.target;if(!(g instanceof Element))return;let v=x(g);v&&(m=v,I(),u=setTimeout(()=>a(v),n),w(v,g))}function C(i){let g=i.target;if(!(g instanceof Element))return;let v=x(g);v&&setTimeout(()=>{m===v&&(m=null),I(),w(v,g)},300)}function S(i){i.target instanceof HTMLFormElement&&s()}let N=new MutationObserver(i=>{f||(f=!0,P(()=>{_(i),f=!1}))});function _(i){let g=new Set;for(let v of i){if(v.type==="attributes"&&v.target instanceof Element){let k=x(v.target);k&&g.add(k)}if(v.type==="childList"){for(let k of v.addedNodes)k instanceof Element&&H(k,g);for(let k of v.removedNodes)k instanceof Element&&H(k,g)}}for(let v of g){let k=document.querySelector(v);k&&w(v,k)}}let F=new IntersectionObserver(i=>{for(let g of i){let v=A.get(g.target);v&&l.set(v,g.isIntersecting)}},{threshold:.1}),A=new Map;function w(i,g){let v={hasValue:y(g),hasError:L(g),errorText:q(g),hasFocus:m===i},k=d.get(i);(!k||E(k,v))&&(d.set(i,v),o(i,v))}function E(i,g){return i.hasValue!==g.hasValue||i.hasError!==g.hasError||i.errorText!==g.errorText||i.hasFocus!==g.hasFocus}function y(i){return i instanceof HTMLInputElement||i instanceof HTMLTextAreaElement?i.value.length>0:i instanceof HTMLSelectElement?i.selectedIndex>0:!1}function L(i){if(i instanceof HTMLInputElement&&!i.validity.valid||i instanceof HTMLTextAreaElement&&!i.validity.valid||i instanceof HTMLSelectElement&&!i.validity.valid||i.getAttribute("aria-invalid")==="true")return!0;let g=i.className.toString().toLowerCase();if(/\b(error|invalid|danger|has-error|is-invalid|field-error|ng-invalid)\b/.test(g))return!0;let v=i.parentElement;if(v){let k=v.className.toString().toLowerCase();if(/\b(error|invalid|has-error|field-error)\b/.test(k))return!0}return!1}function q(i){let g=i.getAttribute("aria-errormessage");if(g){let R=document.getElementById(g);if(R&&M(R))return R.textContent?.trim()||null}let v=i.getAttribute("aria-describedby");if(v)for(let R of v.split(/\s+/)){let U=document.getElementById(R);if(U&&M(U)&&/error|invalid|help/.test(U.className.toLowerCase()))return U.textContent?.trim()||null}let k=i.nextElementSibling;if(k&&M(k)&&/error|invalid|validation|field-error/.test(k.className.toLowerCase()))return k.textContent?.trim()||null;let X=i.parentElement;if(X){let R=X.querySelector('[role="alert"], .error-message, .field-error, .validation-message');if(R&&M(R))return R.textContent?.trim()||null}return null}function x(i){if(i.id&&r.has(`#${i.id}`))return`#${i.id}`;for(let g of r)try{if(i.matches(g))return g}catch{}return null}function H(i,g){let v=i.parentElement;if(v)for(let k of r)try{v.querySelector(k)&&g.add(k)}catch{}}function M(i){return i instanceof HTMLElement?i.offsetParent!==null&&!i.hidden:!0}function I(){u!==null&&(clearTimeout(u),u=null)}function P(i){"requestIdleCallback"in window?requestIdleCallback(i,{timeout:100}):setTimeout(i,16)}function B(){document.addEventListener("focusin",h,{passive:!0}),document.addEventListener("focusout",C,{passive:!0}),document.addEventListener("submit",S,{passive:!0,capture:!0}),N.observe(document.body,{subtree:!0,childList:!0,attributes:!0,attributeFilter:["class","aria-invalid","aria-errormessage","aria-describedby","disabled","hidden","aria-hidden"],characterData:!1});for(let i of r)try{let g=document.querySelector(i);g&&(A.set(g,i),F.observe(g),l.set(i,!0))}catch{}for(let i of r){let g=document.querySelector(i);g&&w(i,g)}}function G(){document.removeEventListener("focusin",h),document.removeEventListener("focusout",C),document.removeEventListener("submit",S,{capture:!0}),N.disconnect(),F.disconnect(),I(),A.clear(),d.clear(),l.clear()}function K(){let i={};for(let[g,v]of d)i[g]=v;return i}function $(){return Array.from(l.entries()).filter(([,i])=>i).map(([i])=>i)}return{start:B,destroy:G,getFieldStates:K,getVisibleFieldIds:$}}function Te(e,t){return e.when.split(/\s+AND\s+/i).every(o=>Le(o.trim(),t))}function ae(e,t){return e.filter(n=>Te(n,t))}function Le(e,t){let n=e.match(/^\[([^\]]+)\]\.error(?:\.(\w+))?$/);if(n){let o=t.fieldStates[n[1]];return!o||!o.hasError?!1:n[2]&&o.errorText?o.errorText.toLowerCase().includes(n[2].toLowerCase()):o.hasError}if(n=e.match(/^\[([^\]]+)\]\.empty$/),n){let o=t.fieldStates[n[1]];return o?!o.hasValue:!0}return n=e.match(/^\[([^\]]+)\]\.changed$/),n?t.changedFields.has(n[1]):(n=e.match(/^\[([^\]]+)\]\.focus$/),n?t.fieldStates[n[1]]?.hasFocus??!1:(n=e.match(/^page\.idle\s*>\s*(\d+)s$/),n?t.idleSeconds>=parseInt(n[1],10):e==="form.incomplete"?t.hasIncompleteRequired:(n=e.match(/^user\.attempts\s*>\s*(\d+)$/),n?t.submitAttempts>parseInt(n[1],10):!1)))}var Fe={rage_click:3,exit_intent:2.5,validation_error:2,same_field_error:2.5,slow_dwell:1.5,field_cycling:1.5,repeated_correction:1,dead_click:1},Ae=2*60*1e3;function le(e){let t=[],n=0,o=0,a=e.threshold,s=new Map,r=null,d=0,l=[],m=new Map,u=new Set,f=[];function h(w){let E=Date.now();t.push({signal:w,weight:Fe[w],ts:E}),F(E),C()>=a&&N(E)&&e.onFrustrated()}function C(){let w=Date.now();return F(w),t.reduce((E,y)=>E+y.weight,0)}function S(){t.length=0}function N(w){if(o===0)return!0;let E=[6e4,12e4,24e4],y=E[Math.min(n-1,E.length-1)]??6e4;return w-o>=y}function _(){n++,o=Date.now(),n>=3&&(a=7.5),S()}function F(w){let E=w-Ae;for(;t.length>0&&t[0].ts<E;)t.shift()}function A(){function w(q){let x=q.target;if(!(x instanceof Element))return;let H=x.id||x.className.toString().slice(0,50);if(!H)return;let M=Date.now(),I=s.get(H)??[];I.push(M);let P=I.filter(B=>M-B<1e3);s.set(H,P),P.length>=3&&(h("rage_click"),s.set(H,[])),x instanceof HTMLInputElement||x instanceof HTMLButtonElement||x instanceof HTMLAnchorElement||x instanceof HTMLSelectElement||x instanceof HTMLTextAreaElement||x.getAttribute("role")==="button"||setTimeout(()=>{h("dead_click")},2e3)}function E(q){q.clientY<=0&&h("exit_intent")}function y(q){let x=q.target;if(!(x instanceof HTMLInputElement||x instanceof HTMLTextAreaElement||x instanceof HTMLSelectElement))return;let H=x.id?`#${x.id}`:x.name||"",M=Date.now();r&&M-d>3e4&&h("slow_dwell"),r=H,d=M,l.push(M),l=l.filter(I=>M-I<1e4),l.length>=5&&(h("field_cycling"),l=[])}function L(q){let x=q.target;if(!(x instanceof HTMLInputElement||x instanceof HTMLTextAreaElement))return;let H=x.id?`#${x.id}`:x.name||"";if(!H)return;let M=Date.now();if(x.value.length===0){let I=m.get(H)??[];I.push(M);let P=I.filter(B=>M-B<3e4);m.set(H,P),P.length>=3&&(h("repeated_correction"),m.set(H,[]))}}document.addEventListener("click",w,{passive:!0}),document.documentElement.addEventListener("mouseleave",E,{passive:!0}),document.addEventListener("focusin",y,{passive:!0}),document.addEventListener("input",L,{passive:!0}),f.push(()=>document.removeEventListener("click",w),()=>document.documentElement.removeEventListener("mouseleave",E),()=>document.removeEventListener("focusin",y),()=>document.removeEventListener("input",L))}return A(),{recordSignal:h,getScore:C,reset:S,recordValidationError(w){u.has(w)?h("same_field_error"):(u.add(w),h("validation_error"))},onDismiss:_,destroy(){for(let w of f)w();f.length=0,t.length=0}}}function Me(e,t,n,o){let a=[],s=[...e].sort((r,d)=>r.priority-d.priority);for(let r of s){if(!Z(r.when,n))continue;let m=`${t.some(u=>`[${u.id}]`===r.target||u.id===r.target)?"ls:section":"ls:field"}:${r.action}`;a.push({type:m,target:r.target,rule:r.when,pageId:o})}return a}function He(e){for(let t of e)document.dispatchEvent(new CustomEvent("ls:workflow",{detail:t,bubbles:!0,composed:!0}))}function Z(e,t){if(e.includes(" AND "))return e.split(" AND ").every(d=>Z(d.trim(),t));if(e.includes(" OR "))return e.split(" OR ").some(d=>Z(d.trim(),t));let n=e.trim(),o=n.match(/^\[([^\]]+)\]\.checked$/i);if(o)return t.checkedFields.has(o[1]);let a=n.match(/^\[([^\]]+)\]\.value\s*==\s*"([^"]*)"$/i);if(a)return(t.fieldValues[a[1]]??"").toLowerCase()===a[2].toLowerCase();let s=n.match(/^\[([^\]]+)\]\.empty$/i);if(s)return(t.fieldValues[s[1]]??"")==="";let r=n.match(/^\[([^\]]+)\]\.hasValue$/i);return r?(t.fieldValues[r[1]]??"")!=="":!1}function ce(e,t,n){let o=[];function a(){let r=s(t),d=Me(e,t,r,n),l=d.map(u=>`${u.type}:${u.target}`);(l.length!==o.length||l.some((u,f)=>u!==o[f]))&&(o=l,He(d))}function s(r){let d={},l=new Set,m=new Set;for(let u of r)for(let f of u.fields)m.add(f);for(let u of e){let f=u.when.matchAll(/\[([^\]]+)\]/g);for(let h of f)m.add(h[1])}for(let u of m){let f=document.querySelector(u);f&&((f.type==="checkbox"||f.type==="radio")&&f.checked&&l.add(u),d[u]=f.value??"")}return{fieldValues:d,checkedFields:l}}return{evaluate:a,destroy(){o=[]}}}var de=`
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

/* \u2500\u2500 Field Help Inline (persistent until cleared) \u2500\u2500 */
.ls-field-help {
  position: fixed;
  background: var(--ls-bg);
  border: 1px solid var(--ls-border);
  border-radius: 12px;
  padding: 12px 16px;
  box-shadow: var(--ls-shadow);
  max-width: 320px;
  z-index: 2147483645;
  opacity: 0;
  transform: translateY(8px);
  transition: opacity 0.2s ease-out, transform 0.2s ease-out;
  pointer-events: none;
}

.ls-field-help-visible {
  opacity: 1;
  transform: translateY(0);
  pointer-events: auto;
}

.ls-field-help-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 8px;
}

.ls-field-help-label {
  font-weight: 600;
  font-size: 13px;
  color: var(--ls-text);
}

.ls-field-help-close {
  background: none;
  border: none;
  cursor: pointer;
  color: var(--ls-text-secondary);
  padding: 2px;
  border-radius: 4px;
  display: flex;
  font-size: 14px;
  line-height: 1;
}

.ls-field-help-close:hover {
  background: var(--ls-bg-secondary);
  color: var(--ls-text);
}

.ls-field-help-text {
  font-size: 13px;
  color: var(--ls-text-secondary);
  line-height: 1.5;
  margin-bottom: 8px;
}

.ls-field-help-format {
  font-size: 11px;
  color: var(--ls-primary);
  font-family: 'SF Mono', 'Fira Code', 'Consolas', monospace;
  background: var(--ls-bg-secondary);
  padding: 4px 8px;
  border-radius: 4px;
}

.ls-field-help-questions {
  display: flex;
  flex-direction: column;
  gap: 4px;
  margin-top: 10px;
  padding-top: 10px;
  border-top: 1px solid var(--ls-border);
}

.ls-field-help-question {
  font-size: 12px;
  color: var(--ls-primary);
  cursor: pointer;
  padding: 4px 0;
  text-decoration: none;
  background: none;
  border: none;
  text-align: left;
}

.ls-field-help-question:hover {
  text-decoration: underline;
}

/* \u2500\u2500 Success Tick Animation (SweetAlert style) \u2500\u2500 */
.ls-success-tick {
  position: fixed;
  width: 60px;
  height: 60px;
  z-index: 2147483646;
  pointer-events: none;
  display: flex;
  align-items: center;
  justify-content: center;
  opacity: 0;
  transform: scale(0);
}

.ls-success-tick-visible {
  animation: ls-tick-pop 0.6s cubic-bezier(0.175, 0.885, 0.32, 1.275) forwards;
}

@keyframes ls-tick-pop {
  0% { opacity: 0; transform: scale(0); }
  50% { opacity: 1; transform: scale(1.2); }
  70% { transform: scale(0.9); }
  100% { opacity: 1; transform: scale(1); }
}

.ls-success-tick-circle {
  width: 56px;
  height: 56px;
  border-radius: 50%;
  background: var(--ls-success);
  display: flex;
  align-items: center;
  justify-content: center;
  box-shadow: 0 4px 16px rgba(34, 197, 94, 0.4);
}

.ls-success-tick svg {
  width: 32px;
  height: 32px;
  stroke: white;
  stroke-width: 3;
  stroke-linecap: round;
  stroke-linejoin: round;
  fill: none;
}

.ls-success-tick-path {
  stroke-dasharray: 50;
  stroke-dashoffset: 50;
  animation: ls-tick-draw 0.4s ease-out 0.2s forwards;
}

@keyframes ls-tick-draw {
  to { stroke-dashoffset: 0; }
}

.ls-success-tick-fade {
  animation: ls-tick-fade 0.3s ease-out 0.8s forwards;
}

@keyframes ls-tick-fade {
  to { opacity: 0; transform: scale(0.8); }
}

/* \u2500\u2500 Cached Response Indicator \u2500\u2500 */
.ls-response-cached {
  display: flex;
  align-items: center;
  gap: 4px;
  font-size: 10px;
  color: var(--ls-text-secondary);
  margin-top: 8px;
}

.ls-response-cached svg {
  width: 12px;
  height: 12px;
  fill: currentColor;
}
`;var _e='<svg viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg"><path d="M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 17h-2v-2h2v2zm2.07-7.75l-.9.92C13.45 12.9 13 13.5 13 15h-2v-.5c0-1.1.45-2.1 1.17-2.83l1.24-1.26c.37-.36.59-.86.59-1.41 0-1.1-.9-2-2-2s-2 .9-2 2H8c0-2.21 1.79-4 4-4s4 1.79 4 4c0 .88-.36 1.68-.93 2.25z"/></svg>',Ie='<svg viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg"><path d="M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z"/></svg>',Ne='<svg viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg"><path d="M2.01 21L23 12 2.01 3 2 10l15 2-15 2z"/></svg>';function ue(e,t,n,o){let a=document.createElement("style");a.textContent=de,e.appendChild(a);let s=document.createElement("div");s.className="ls-root",s.setAttribute("data-theme",n),s.setAttribute("data-position",t);let r=document.createElement("button");r.className="ls-fab",r.setAttribute("aria-label","Open help"),r.innerHTML=_e,r.addEventListener("click",o.onFabClick);let d=document.createElement("div");d.className="ls-toast-container",d.setAttribute("role","status"),d.setAttribute("aria-live","polite");let l=document.createElement("div");l.className="ls-panel",l.setAttribute("role","dialog"),l.setAttribute("aria-label","Help panel");let m=document.createElement("div");m.className="ls-panel-header";let u=document.createElement("span");u.className="ls-panel-title",u.textContent="Help";let f=document.createElement("button");f.className="ls-panel-close",f.setAttribute("aria-label","Close help panel"),f.innerHTML=Ie,f.addEventListener("click",o.onPanelClose),m.appendChild(u),m.appendChild(f);let h=document.createElement("div");h.className="ls-panel-body";let C=document.createElement("div");C.className="ls-response";let S=document.createElement("div");S.className="ls-suggestions";let N=document.createElement("div");N.className="ls-topics",h.appendChild(C),h.appendChild(S),h.appendChild(N);let _=document.createElement("div");_.className="ls-panel-input";let F=document.createElement("input");F.type="text",F.className="ls-input",F.placeholder="Ask a question...",F.setAttribute("aria-label","Ask a help question");let A=document.createElement("button");A.className="ls-send",A.setAttribute("aria-label","Send"),A.innerHTML=Ne;function w(){let E=F.value.trim();E&&(o.onSendMessage(E),F.value="")}return A.addEventListener("click",w),F.addEventListener("keydown",E=>{E.key==="Enter"&&!E.shiftKey&&(E.preventDefault(),w())}),_.appendChild(F),_.appendChild(A),l.appendChild(m),l.appendChild(h),l.appendChild(_),s.appendChild(r),s.appendChild(d),s.appendChild(l),e.appendChild(s),{root:s,fab:r,toastContainer:d,panel:l,panelBody:h,responseArea:C,suggestionsArea:S,topicsArea:N,input:F,sendBtn:A}}var D=new Map;function fe(e,t,n){let o=document.createElement("div");o.className="ls-toast",o.setAttribute("role","status");let a=document.createElement("span");a.className="ls-toast-text",a.textContent=t.suggest;let s=document.createElement("button");s.className="ls-toast-dismiss",s.setAttribute("aria-label","Dismiss"),s.textContent="\xD7",o.appendChild(a),o.appendChild(s),a.addEventListener("click",()=>{ee(o),n(t)}),s.addEventListener("click",d=>{d.stopPropagation(),ee(o)}),e.appendChild(o),requestAnimationFrame(()=>{requestAnimationFrame(()=>o.classList.add("ls-toast-show"))});let r=setTimeout(()=>ee(o),1e4);return D.set(o,r),o}function ee(e){let t=D.get(e);t&&clearTimeout(t),D.delete(e),e.classList.remove("ls-toast-show"),e.addEventListener("transitionend",()=>e.remove(),{once:!0}),setTimeout(()=>e.remove(),400)}function Q(e){for(let[t,n]of D)clearTimeout(n),t.remove();D.clear()}function oe(e,t){e.classList.add("ls-panel-open"),t.classList.add("ls-fab-hidden")}function me(e,t){e.classList.remove("ls-panel-open"),t.classList.remove("ls-fab-hidden")}function ge(e,t,n){e.responseArea.textContent=t.text,e.suggestionsArea.textContent="";for(let o of t.suggestions){let a=document.createElement("button");a.className="ls-chip",a.textContent=o,a.addEventListener("click",()=>n.onChipClick(o)),e.suggestionsArea.appendChild(a)}e.topicsArea.textContent="";for(let o of t.topics){let a=document.createElement("a");a.className="ls-topic-link",a.textContent=o.label,a.href="#",a.addEventListener("click",s=>{s.preventDefault(),n.onTopicClick(o.id)}),e.topicsArea.appendChild(a)}}function he(e){e.responseArea.textContent="";let t=document.createElement("div");t.className="ls-spinner";for(let n=0;n<3;n++){let o=document.createElement("div");o.className="ls-spinner-dot",t.appendChild(o)}e.responseArea.appendChild(t)}function ve(e,t){e.responseArea.textContent=t}function xe(e,t){let n=document.createElement("div");n.className="ls-context",n.textContent=`I can help you with the ${t} page. Ask a question or click a suggestion below.`,e.panelBody.insertBefore(n,e.responseArea)}var z=new Map,Y=null;function V(e,t,n){let o=document.querySelector(t);if(!o)return;let a=o.getBoundingClientRect(),s=document.createElement("div");s.className=`ls-highlight ls-highlight-${n}`,s.style.position="fixed",s.style.top=`${a.top-3}px`,s.style.left=`${a.left-3}px`,s.style.width=`${a.width+6}px`,s.style.height=`${a.height+6}px`,s.style.pointerEvents="none";let r=e.querySelector(".ls-root");r&&r.appendChild(s),requestAnimationFrame(()=>s.classList.add("ls-highlight-active")),z.set(s,t),z.size===1&&qe(),setTimeout(()=>{s.classList.remove("ls-highlight-active"),s.classList.add("ls-highlight-fade"),s.addEventListener("transitionend",()=>{s.remove(),z.delete(s),z.size===0&&be()},{once:!0}),setTimeout(()=>{s.remove(),z.delete(s)},400)},5e3)}function J(){for(let[e]of z)e.remove();z.clear(),be()}function qe(){function e(){for(let[t,n]of z){let o=document.querySelector(n);if(!o){t.remove(),z.delete(t);continue}let a=o.getBoundingClientRect();t.style.top=`${a.top-3}px`,t.style.left=`${a.left-3}px`,t.style.width=`${a.width+6}px`,t.style.height=`${a.height+6}px`}z.size>0&&(Y=requestAnimationFrame(e))}Y=requestAnimationFrame(e)}function be(){Y!==null&&(cancelAnimationFrame(Y),Y=null)}function ye(e,t){let n=document.createElement("div");n.className="ls-toast ls-toast-struggling",n.setAttribute("role","status");let o=document.createElement("div");o.className="ls-toast-text",o.textContent="Need help with this form?";let a=document.createElement("div");a.className="ls-struggling-actions";let s=document.createElement("button");s.className="ls-struggling-guide",s.textContent="Guide me through it",s.addEventListener("click",l=>{l.stopPropagation(),te(n),t.onAcceptGuide()});let r=document.createElement("button");r.className="ls-toast-dismiss",r.setAttribute("aria-label","Dismiss"),r.textContent="\xD7",r.addEventListener("click",l=>{l.stopPropagation(),te(n),t.onDismissStruggling()}),a.appendChild(s),a.appendChild(r),n.appendChild(o),n.appendChild(a),e.appendChild(n),requestAnimationFrame(()=>{requestAnimationFrame(()=>n.classList.add("ls-toast-show"))});let d=setTimeout(()=>{te(n),t.onDismissStruggling()},15e3);return D.set(n,d),n}function te(e){let t=D.get(e);t&&clearTimeout(t),D.delete(e),e.classList.remove("ls-toast-show"),e.addEventListener("transitionend",()=>e.remove(),{once:!0}),setTimeout(()=>e.remove(),400)}var pe=null;function we(e,t,n,o,a){a&&(pe=a),e.panelBody.textContent="";let s=document.createElement("div");s.className="ls-guide-progress";let r=document.createElement("div");r.className="ls-progress-bar";let d=t.fields.length>0?Math.round(o.size/t.fields.length*100):0;r.style.width=`${d}%`;let l=document.createElement("span");if(l.className="ls-progress-text",l.textContent=`${o.size} of ${t.fields.length} fields complete`,s.appendChild(r),s.appendChild(l),e.panelBody.appendChild(s),n>=0&&n<t.fields.length){let u=t.fields[n],f=document.createElement("div");f.className="ls-guide-current",f.setAttribute("aria-live","polite");let h=document.createElement("div");if(h.className="ls-guide-field-label",h.textContent=u.label,f.appendChild(h),u.help){let C=document.createElement("div");C.className="ls-guide-help",C.textContent=u.help,f.appendChild(C)}if(u.pattern){let C=document.createElement("div");C.className="ls-guide-format",C.textContent=`Format: ${u.pattern}`,f.appendChild(C)}if(e.panelBody.appendChild(f),n+1<t.fields.length){let C=t.fields[n+1],S=document.createElement("div");S.className="ls-guide-next",S.textContent=`Next: ${C.label}`,e.panelBody.appendChild(S)}}e.panelBody.appendChild(e.responseArea),e.panelBody.appendChild(e.suggestionsArea),e.panelBody.appendChild(e.topicsArea);let m=document.createElement("button");m.className="ls-guide-exit",m.textContent="Exit guided mode",m.addEventListener("click",()=>{pe?.()}),e.panelBody.appendChild(m)}function Ee(e,t,n,o){let a=e.panelBody.querySelector(".ls-progress-bar"),s=e.panelBody.querySelector(".ls-progress-text");if(a&&s){let l=t.fields.length>0?Math.round(o.size/t.fields.length*100):0;a.style.width=`${l}%`,s.textContent=`${o.size} of ${t.fields.length} fields complete`}let r=e.panelBody.querySelector(".ls-guide-current");if(r&&n>=0&&n<t.fields.length){let l=t.fields[n],m=r.querySelector(".ls-guide-field-label");m&&(m.textContent=l.label);let u=r.querySelector(".ls-guide-help");u&&(u.textContent=l.help??"");let f=r.querySelector(".ls-guide-format");f&&(f.textContent=l.pattern?`Format: ${l.pattern}`:"")}let d=e.panelBody.querySelector(".ls-guide-next");d&&(n+1<t.fields.length?d.textContent=`Next: ${t.fields[n+1].label}`:d.textContent="All fields covered!")}function ke(){if(window.matchMedia("(prefers-color-scheme: dark)").matches)return"dark";let e=getComputedStyle(document.body).backgroundColor,t=Re(e);return t&&$e(t)<.5?"dark":"light"}function Re(e){let t=e.match(/rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)/);return t?[parseInt(t[1]),parseInt(t[2]),parseInt(t[3])]:null}function $e([e,t,n]){return(.2126*e+.7152*t+.0722*n)/255}var j=null,ne=null;function Ce(e,t,n){O();let o=document.querySelector(t);if(!o)return;let a=e.querySelector(".ls-root");if(!a)return;let s=o.getBoundingClientRect(),r=document.createElement("div");r.className="ls-field-help",window.innerHeight-s.bottom<180&&s.top>180?r.style.bottom=`${window.innerHeight-s.top+8}px`:r.style.top=`${s.bottom+8}px`,r.style.left=`${Math.max(12,Math.min(s.left,window.innerWidth-340))}px`;let m=document.createElement("div");m.className="ls-field-help-header";let u=document.createElement("span");u.className="ls-field-help-label",u.textContent=n.label;let f=document.createElement("button");if(f.className="ls-field-help-close",f.textContent="\xD7",f.setAttribute("aria-label","Close help"),f.addEventListener("click",h=>{h.stopPropagation(),O()}),m.appendChild(u),m.appendChild(f),r.appendChild(m),n.help){let h=document.createElement("div");h.className="ls-field-help-text",h.textContent=n.help,r.appendChild(h)}if(n.pattern){let h=document.createElement("div");h.className="ls-field-help-format",h.textContent=`Format: ${n.pattern}`,r.appendChild(h)}if(n.questions&&n.questions.length>0){let h=document.createElement("div");h.className="ls-field-help-questions";for(let C of n.questions){let S=document.createElement("button");S.className="ls-field-help-question",S.textContent=C,S.addEventListener("click",N=>{N.stopPropagation(),n.onQuestionClick?.(C)}),h.appendChild(S)}r.appendChild(h)}a.appendChild(r),j=r,ne=n.onClose||null,requestAnimationFrame(()=>{requestAnimationFrame(()=>r.classList.add("ls-field-help-visible"))})}function O(){if(j){j.classList.remove("ls-field-help-visible");let e=j;j=null,ne?.(),ne=null,e.addEventListener("transitionend",()=>e.remove(),{once:!0}),setTimeout(()=>e.remove(),300)}}function se(e,t,n=1200){return new Promise(o=>{let a=document.querySelector(t);if(!a){o();return}let s=e.querySelector(".ls-root");if(!s){o();return}let r=a.getBoundingClientRect(),d=document.createElement("div");d.className="ls-success-tick",d.style.top=`${r.top+r.height/2-30}px`,d.style.left=`${r.left+r.width/2-30}px`;let l=document.createElement("div");l.className="ls-success-tick-circle";let m=document.createElementNS("http://www.w3.org/2000/svg","svg");m.setAttribute("viewBox","0 0 24 24");let u=document.createElementNS("http://www.w3.org/2000/svg","path");u.setAttribute("d","M5 13l4 4L19 7"),u.classList.add("ls-success-tick-path"),m.appendChild(u),l.appendChild(m),d.appendChild(l),s.appendChild(d),requestAnimationFrame(()=>{d.classList.add("ls-success-tick-visible"),setTimeout(()=>{d.classList.add("ls-success-tick-fade")},n-400),setTimeout(()=>{d.remove(),o()},n)})})}function Se(e){if(e.responseArea.querySelector(".ls-response-cached"))return;let t=document.createElement("div");t.className="ls-response-cached",t.innerHTML=`
    <svg viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">
      <path d="M13 3a9 9 0 0 0-9 9H1l3.89 3.89.07.14L9 12H6c0-3.87 3.13-7 7-7s7 3.13 7 7-3.13 7-7 7c-1.93 0-3.68-.79-4.94-2.06l-1.42 1.42A8.954 8.954 0 0 0 13 21a9 9 0 0 0 0-18zm-1 5v5l4.28 2.54.72-1.21-3.5-2.08V8H12z"/>
    </svg>
    <span>Instant answer</span>
  `,e.responseArea.appendChild(t)}var ze=[{from:"idle",event:"condition_trigger",to:"toast"},{from:"idle",event:"fab_click",to:"panel"},{from:"idle",event:"field_idle",to:"toast"},{from:"toast",event:"toast_click",to:"panel"},{from:"toast",event:"toast_dismiss",to:"idle"},{from:"toast",event:"toast_timeout",to:"idle"},{from:"panel",event:"ask_question",to:"asking"},{from:"panel",event:"panel_close",to:"idle"},{from:"asking",event:"response",to:"showing"},{from:"asking",event:"error",to:"panel"},{from:"showing",event:"panel_close",to:"idle"},{from:"showing",event:"ask_question",to:"asking"},{from:"idle",event:"frustration_threshold",to:"struggling"},{from:"struggling",event:"accept_guide",to:"active"},{from:"struggling",event:"dismiss_struggling",to:"idle"},{from:"struggling",event:"fab_click",to:"panel"},{from:"active",event:"exit_guide",to:"idle"},{from:"active",event:"ask_question",to:"asking"},{from:"showing",event:"exit_guide",to:"idle"}];function Pe(e,t){let n=ze.find(o=>o.from===e&&o.event===t);return n?n.to:null}function tt(e,t){let n="idle",o=null,a=null,s=null,r=null,d=null,l={fieldStates:{},changedFields:new Set,idleSeconds:0,hasIncompleteRequired:!1,submitAttempts:0},m=-1,u=new Set,f=null,h=Date.now(),C=new Set,S=new Map,N=30*60*1e3,_=null,F=!1,A=re(t.api),w=t.theme==="auto"?ke():t.theme,E={onFabClick:()=>L("fab_click"),onToastClick:c=>{s=c,L("toast_click")},onPanelClose:()=>{L(n==="active"?"exit_guide":"panel_close")},onSendMessage:c=>x(c),onChipClick:c=>x(c),onTopicClick:c=>H(c),onAcceptGuide:()=>L("accept_guide"),onDismissStruggling:()=>{r?.onDismiss(),L("dismiss_struggling")},onExitGuide:()=>L("exit_guide")},y=ue(e,t.position,w,E);function L(c){let p=Pe(n,c);if(p===null)return;let b=n;n=p,q(b,p,c)}function q(c,p,b){switch(p){case"idle":me(y.panel,y.fab),Q(y.toastContainer),J(),u.clear(),m=-1;break;case"toast":s&&fe(y.toastContainer,s,T=>{s=T,L("toast_click")});break;case"panel":Q(y.toastContainer),oe(y.panel,y.fab),o&&(xe(y,o.title),P()),c==="toast"&&s&&(y.responseArea.textContent=s.suggest,s.highlight&&V(e,s.highlight,"info")),y.input.focus();break;case"asking":he(y);break;case"showing":break;case"struggling":ye(y.toastContainer,E);break;case"active":Q(y.toastContainer),oe(y.panel,y.fab),u.clear(),m=0,o&&(we(y,o,m,u,()=>L("exit_guide")),o.fields.length>0&&V(e,o.fields[0].selector,"info"));break}A.trackEvent("state_change",{from:c,to:p,event:b})}async function x(c,p=!1){let b=S.get(c);if(b&&Date.now()-b.cachedAt<N){I(b.response,!0),L("ask_question"),L("response");return}if(p)return;L("ask_question");let T=B(c);try{let W=await A.askForHelp(T);S.set(c,{question:c,response:W,cachedAt:Date.now()}),I(W,!1),L("response")}catch{ve(y,"Sorry, help is temporarily unavailable. Please try again."),L("error")}}function H(c){let p=o?.topics.find(b=>b.articleId===c);p&&x(p.question)}async function M(){if(o?.topics.length){for(let c of o.topics)if(!S.has(c.question))try{let p=B(c.question),b=await A.askForHelp(p);S.set(c.question,{question:c.question,response:b,cachedAt:Date.now()})}catch{}}}function I(c,p=!1){ge(y,c,E),p&&Se(y);for(let b of c.highlights)V(e,b.selector,b.style)}function P(){if(o?.topics.length){y.suggestionsArea.textContent="";for(let c of o.topics){let p=document.createElement("button");p.className="ls-chip",p.textContent=c.question,p.addEventListener("click",()=>x(c.question)),y.suggestionsArea.appendChild(p)}}}function B(c){let p=a?.getFieldStates()??{},b=a?.getVisibleFieldIds()??[];return{url:location.pathname,visibleFieldIds:b,fieldStates:p,viewportWidth:window.innerWidth,question:c}}function G(){if(!o?.conditions.length||n!=="idle")return;let c=ae(o.conditions,l);for(let p of c)if(!C.has(p.when)){C.add(p.when),s=p,L("condition_trigger");break}}function K(c,p){if(!(n!=="active"||!o)){if(p.hasValue&&!p.hasError?(u.add(c),V(e,c,"success")):u.delete(c),p.hasFocus){let b=o.fields.findIndex(T=>T.selector===c);if(b>=0&&b!==m){m=b,J(),V(e,c,"info");for(let T of u)V(e,T,"success")}}Ee(y,o,m,u)}}function $(){h=Date.now(),l.idleSeconds=0}function i(){document.addEventListener("mousemove",$,{passive:!0}),document.addEventListener("keydown",$,{passive:!0}),document.addEventListener("scroll",$,{passive:!0}),document.addEventListener("touchstart",$,{passive:!0}),f=setInterval(()=>{l.idleSeconds=Math.floor((Date.now()-h)/1e3),G()},5e3)}function g(){document.removeEventListener("mousemove",$),document.removeEventListener("keydown",$),document.removeEventListener("scroll",$),document.removeEventListener("touchstart",$),f&&clearInterval(f)}function v(c,p){let b=l.fieldStates[c];if(l.fieldStates[c]=p,b&&(b.hasValue!==p.hasValue||b.hasError!==p.hasError)&&l.changedFields.add(c),p.hasError&&(!b||!b.hasError)&&r?.recordValidationError(c),p.hasFocus&&_!==c)_=c,k(c);else if(!p.hasFocus&&_===c){let T=b?.hasValue??!1,W=p.hasValue;!T&&W&&!p.hasError?(O(),se(e,c)):O(),_=null,F=!1}b&&!b.hasValue&&p.hasValue&&!p.hasError&&n!=="active"&&(p.hasFocus||se(e,c)),o?.fields&&(l.hasIncompleteRequired=o.fields.filter(T=>T.type==="required"||T.help?.includes("required")).some(T=>{let W=l.fieldStates[T.selector];return!W||!W.hasValue})),K(c,p),d?.evaluate(),G()}function k(c){if(n==="active")return;let p=o?.fields.find(T=>T.selector===c);if(!p||!p.help&&!p.pattern)return;let b=o?.topics.filter(T=>T.question.toLowerCase().includes(p.label.toLowerCase())||p.label.toLowerCase().includes(T.question.toLowerCase().split(" ")[0])).map(T=>T.question).slice(0,3)??[];Ce(e,c,{label:p.label,help:p.help,pattern:p.pattern,questions:b,onQuestionClick:T=>{O(),x(T)},onClose:()=>{F=!1}}),F=!0}function X(c){if(n!=="idle"||F&&_===c)return;let p=o?.fields.find(b=>b.selector===c);p?.help&&(s={when:`[${c}].focus`,suggest:p.help,highlight:c},L("field_idle"))}function R(){l.submitAttempts++,l.changedFields.clear(),G()}async function U(){o=await A.loadPageModel(location.pathname),o&&(a=ie({trackedSelectors:o.fields.map(c=>c.selector),fieldIdleMs:5e3,onFieldChange:v,onFieldIdle:X,onSubmit:R}),a.start()),o?.workflowRules?.length&&(d=ce(o.workflowRules,o.sections??[],o.pageId),d.evaluate()),o?.topics?.length&&setTimeout(()=>M(),2e3),r=le({threshold:5,onFrustrated:()=>{n==="idle"&&L("frustration_threshold")}}),i(),document.addEventListener("visibilitychange",()=>{document.hidden?g():i()}),A.trackEvent("widget_loaded",{pageId:o?.pageId??"unknown",hasModel:!!o})}return U(),{destroy(){a?.destroy(),r?.destroy(),d?.destroy(),g(),J(),O(),S.clear()}}}export{tt as initWidget};
//# sourceMappingURL=widget.js.map
