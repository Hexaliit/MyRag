<style>
  /* سند RTL — فارسی اصلی؛ کد و بلوک‌های فنی LTR */
  .doc-rtl { direction: rtl; text-align: right; }
  .doc-rtl pre,
  .doc-rtl pre code {
    direction: ltr;
    text-align: left;
    unicode-bidi: isolate;
  }
  .doc-rtl pre {
    display: block;
    width: 100%;
    overflow-x: auto;
  }
  .doc-rtl :not(pre) > code {
    direction: ltr;
    unicode-bidi: isolate;
  }
  .doc-rtl table { direction: rtl; }
  .doc-rtl blockquote { border-left: none; border-right: 4px solid #ccc; padding-left: 0; padding-right: 1em; }
</style>

<div class="doc-rtl" dir="rtl" lang="fa">

# پیشنهاد معماری سوپراپ فرانت‌اند

# Frontend Super-App Architecture Proposal

> - **نسخه:** 1.0.0 —استفاده از Nx به‌عنوان orchestrator مونوریپو / Nx-first Monorepo
> - **تاریخ:** 2026-07-08
> - **مخاطب:** مدیر تیم فنی / Technical Team Lead
> - **وضعیت:** در انتظار تأیید / Pending approval
> - این سند فقط **تصمیم‌ها و اصولِ معماری** را نگه می‌دارد (چرا/چه)، نه جزئیاتِ اجراییِ پرتغییر (چطور/الان چه چیزی نصب است).

### **اسناد مکمل:**

##### گام‌های ساخت از صفر و جزئیات پیاده‌سازی در `SuperApp-Implementation-Progress.md`

##### واژه‌نامه مخفف‌های پروژه سوپر اپ (SuperApp Glossary) `SuperApp-Glossary.md`

##### راهنمای آموزش تیم سوپر اپ (SuperApp) `SupperApp-Learning-Guid.md`

---

## فهرست مطالب / Table of Contents

| بخش / Section | موضوع / Topic                                                                                           |
| ------------- | ------------------------------------------------------------------------------------------------------- |
| ۰             | خلاصه‌ی اجرایی / Executive Summary                                                                      |
| ۱             | پیش‌فرض‌ها و محدودیت‌ها / Assumptions & Constraints                                                     |
| ۲             | تصمیمات معماری کلیدی / Key Architectural Decisions (Federation, Polyrepo, Auth, State, Routing)         |
| ۳             | دیاگرام معماری / Architecture Diagram                                                                   |
| ۴             | پکیج‌های مشترک / Shared Packages                                                                        |
| ۵             | CI/CD / DevOps Pipeline                                                                                 |
| ۶             | فازبندی MVP — PoC محلی (۰–۲) + استقرار روی زیرساخت شرکت (۳–۴)                                           |
| ۷             | 🆕 استراتژی استایل‌دهی / Styling Strategy (Tailwind-first)                                              |
| ۸             | 🆕 مدیریت Server-State (`@superapp/query`) / Server-State Management                                    |
| ۹             | جمع‌بندی موقت / Interim Summary                                                                         |
| ۱۰            | تجربه‌ی توسعه‌دهنده (DX) برای Remote ها / Developer Experience                                          |
| ۱۱            | پشتیبانی موبایل و دسکتاپ / Mobile & Desktop Support                                                     |
| ۱۲            | دیاگرام موبایل/دسکتاپ / Mobile-Desktop Diagram                                                          |
| ۱۳            | ریسک‌ها و راهکارها / Risks & Mitigations                                                                |
| ۱۴            | سوالات تصمیم‌گیری — پاسخ‌ها / Decisions — Answers                                                       |
| ۱۵            | جمع‌بندی نهایی / Final Conclusion                                                                       |
| ۱۶            | نسخه‌بندی مستقل + هم‌راستاسازی توصیه‌شده / Independent Versioning with Recommended Alignment            |
| ۱۷            | Service Worker + Push (FCM) / Push & Caching Subsystem — بدون آفلاین                                    |
| **۱۸**        | 🆕 **Resilience & خطایابی Module Federation** / Federation Resilience (Fallback, Retry, Error Boundary) |
| **۱۹**        | 🆕 **استراتژی تست** / Testing Strategy                                                                  |
| **۲۰**        | 🆕 **امنیت و کارایی** / Security & Performance Hardening                                                |
| **۲۱**        | 🆕 **API Governance و پیکربندی مشترک** / API Governance & Shared Config                                 |
| **۲۲**        | 🆕 **حاکمیت معماری و مالکیت** / Architecture Governance & Ownership                                     |
| **۲۳**        | 🆕 **استراتژی دیپلوی پیشرفته** / Advanced Deployment Strategy                                           |
| **۲۴**        | 🆕 **Observability** / Monitoring & Logging                                                             |

---

## ۰) خلاصه اجرایی / Executive Summary

|                                         |                                                                                                                                                                                                                             |
| --------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **هدف / Goal**                          | طراحی یک **سوپراپ** (Super-App): یک پوسته‌ی واحد (**Shell**) که اپلیکیشن‌های مستقل (**Remote Apps**) را در خود بارگذاری می‌کند، با تجربه‌ی یکپارچه برای کاربر نهایی.                                                        |
| **الگوی معماری / Architecture pattern** | **Micro-Frontends با Module Federation** + **Nx Monorepo (مسیر اصلی؛ Polyrepo فقط در صورت نیاز به ACL سخت)** + پکیج‌های مشترک نسخه‌بندی‌شده                                                                                 |
| **ابزار مونوریپو / Monorepo tooling**   | **Nx** (کش، `affected`، project graph، `@nx/enforce-module-boundaries`) + پلاگین‌های بومیِ Rspack/MF                                                                                                                        |
| **احراز هویت / Auth**                   | یک‌بار در Shell از طریق **Identity Server (OIDC)**؛ توکن **JWT** مشترک بین Shell و همه‌ی Remote ها                                                                                                                          |
| **بک‌اند / Backend**                    | میکروسرویس / Microservices — هر Remote به endpoint های سرویس خودش وصل می‌شود                                                                                                                                                |
| **پلتفرم DevOps**                       | **فاز ۰–۲ (PoC محلی):** توسعه و تست روی سیستم شخصی — بدون Azure. **فاز ۳ (زیرساخت شرکت):** Azure DevOps (Repos, Artifacts, Pipelines) و دیپلوی مرحله‌ای                                                                     |
| **مدیریت پکیج / Package manager**       | **`bun`** (اولویت) — نصب سریع، workspace، dedup بومی؛ **`pnpm`** به‌عنوان مسیر امنِ عقب‌نشینی در صورت محدودیت ابزار/رجیستری (جزئیات: بخش ۱۰-۴)                                                                              |
| **برنامه‌ی زمانی MVP / MVP timeline**   | **~۲ ماه به‌عنوان هدف برنامه‌ریزی** (نه ضرب‌الاجل سخت) — شامل Shell + یک Remote نمونه. کمی طولانی‌تر شدن مشکلی ایجاد نمی‌کند؛ اولویت با **کیفیت و اثبات معماری** است.                                                       |
| **CSS / استایل‌دهی**                    | **Tailwind CSS v4 از فاز ۰** + `cva` (variants) + `clsx`/`tailwind-merge` (ادغام کلاس) + **design tokens با `@theme`**. ایزوله‌سازی در MF با **تمِ مشترک + preflight فقط در Shell**. CSS Modules فقط برای موارد خاص (بخش ۷) |
| **تکنولوژی پایه / Core stack**          | **React** (تثبیت‌شده)، **Module Federation 2.0** (`@module-federation/enhanced`)، TypeScript، Design System مشترک (RTL-first)                                                                                               |

### چرا این معماری؟ / Why this architecture?

این پیشنهاد مستقیماً از پاسخ‌های پرسشنامه‌ی تصمیم‌گیری استخراج شده است. سه محور اصلی پاسخ‌ها، این الگو را به‌تنهایی نتیجه می‌دهند:

| محور / Axis          | پاسخ‌های محرک / Driving answers | نتیجه / Outcome                                       |
| -------------------- | ------------------------------- | ----------------------------------------------------- |
| الگوی Shell + Remote | ۴، ۵، ۶، ۷، ۸، ۹ (همگی «بله»)   | ترکیب در زمان اجرا — نه iframe، نه بیلد یکپارچه       |
| استقلال بیلد/دیپلوی  | ۱۰، ۱۱، ۱۲، ۱۳، ۱۷              | هر Remote مستقل دیپلوی/rollback شود                   |
| کد و منطق مشترک      | ۱۴، ۱۸، ۱۹، ۲۱                  | Design System و منطق مشترک به‌صورت پکیج نسخه‌بندی‌شده |

---

## ۱) پیش‌فرض‌ها و محدودیت‌ها / Assumptions & Constraints

| #   | مورد / Item                           | توضیح / Note                                                                                                                                                 |
| --- | ------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| A1  | React پایه‌ی فنی ثابت است             | فریمورک دیگری روی میز نیست                                                                                                                                   |
| A2  | اپ‌ها داخل‌سازمانی‌اند                | نیازی به SSR / SEO نیست                                                                                                                                      |
| A3  | «پوستاندازی» = بازطراحی ساختار/معماری | نه صرفاً تغییر ظاهر                                                                                                                                          |
| A4  | MVP = Shell + یک Remote نمونه         | برنامه‌ی هدف ~۲ ماه — **نه ضرب‌الاجل سخت**؛ طولانی‌تر شدن مانع نیست                                                                                          |
| A5  | برون‌سپاری فعلاً در دستور کار نیست    | ولی معماری اجازه‌اش را می‌دهد                                                                                                                                |
| A6  | چندزبانه (i18n) فعلاً لازم نیست       | ولی جای توسعه باقی می‌ماند؛ **RTL از روز اول لحاظ می‌شود** (layout، نه ترجمه — بخش ۱۱-۸)                                                                     |
| A7  | نسخه‌ی React هماهنگ در همه‌جا         | `singleton: true` در Federation؛ **React 18** (تصمیم m1 — بخش ۲-۱)                                                                                           |
| A8  | Identity Server + JWT                 | OIDC code flow پیشنهادی                                                                                                                                      |
| A9  | **PoC محلی قبل از Azure**             | فاز ۰–۲ روی سیستم شخصی در یک **Nx Monorepo** با مرزبندیِ tag-محور (`@nx/enforce-module-boundaries`) — بدون Azure، بدون registry جدا                          |
| A10 | **هدف نهایی زیرساخت شرکت**            | Azure DevOps + Artifacts + Pipeline (با `nx affected`) — پس از اثبات معماری در PoC محلی. **split به Polyrepo فقط در صورت نیاز واقعی به ACL سخت**، نه پیش‌فرض |
| A11 | **مدیریت پکیج با `bun`**              | اولویت با `bun`؛ در صورت محدودیت ابزار/رجیستری، `pnpm` (بخش ۱۰-۴)                                                                                            |

---

## ۲) تصمیمات معماری کلیدی / Key Architectural Decisions

### ۲-۱) الگوی کلی: Module Federation 2.0

**تصمیم / Decision:** سوپراپ از یک **Host (Shell)** و چندین **Remote** تشکیل می‌شود که با **Module Federation 2.0** (`@module-federation/enhanced`) در زمان اجرا ترکیب می‌شوند. این کتابخانه با **Webpack، Rspack و Vite** کار می‌کند و runtime آن مستقل از باندلر است.

**چرا Module Federation و نه جایگزین‌ها؟**

| گزینه / Option                            | نقطه قوت                                                          | چرا رد شد؟ / Why rejected                               |
| ----------------------------------------- | ----------------------------------------------------------------- | ------------------------------------------------------- |
| **Module Federation** ✅                  | ترکیب در زمان اجرا، state مشترک، shared singletons، پشتیبانی رسمی | انتخاب شد                                               |
| iframe                                    | ایزوله‌سازی قوی                                                   | UX ضعیف، full reload، عدم اشتراک state (نقض سوال ۸ و ۹) |
| Single-SPA                                | بلوغ بالا                                                         | نیاز به پیکربندی دستی بیشتر، shared singleton سخت‌تر    |
| Build-time composition / Monolithic build | سادگی                                                             | عدم استقلال دیپلوی (نقض سوال ۱۰–۱۳)                     |
| NPM package import در زمان بیلد           | ساده                                                              | هر تغییر نیاز به بیلد مجدد کل Shell دارد (نقض سوال ۱۲)  |

#### چرا Module Federation **2.0** و نه MF کلاسیک (Webpack v1)؟ — ADR-0001

نسخه‌ی کلاسیک Module Federation (Webpack 5) قابلیت‌های زیادی را به توسعه‌دهنده واگذار می‌کند که باید **دستی بازسازی** شوند. **MF 2.0** (`@module-federation/enhanced`) این‌ها را به‌صورت **بومی** فراهم می‌کند:

| قابلیت                                | MF کلاسیک (v1)              | MF 2.0 (`@module-federation/enhanced`)                  |
| ------------------------------------- | --------------------------- | ------------------------------------------------------- |
| Manifest مرکزی                        | باید دستی ساخته شود (بخش ۳) | `@module-federation/manifest` بومی + `mf-manifest.json` |
| RemoteLoader + Retry + Error handling | کد سفارشی (بخش ۱۸)          | Runtime plugin با hook `errorLoadRemote`                |
| اشتراک type بین Remote/Shell          | type-gen سفارشی (بخش ۱۹-۳)  | `@module-federation/dts-plugin` (type sharing خودکار)   |
| Version negotiation پیشرفته           | محدود                       | مدیریت پیشرفته‌ی shared در runtime + `shareStrategy`    |
| Dynamic remotes / override            | دستی (بخش ۱۰)               | `registerRemotes` runtime API                           |
| ابزار دیباگ                           | ندارد                       | **Chrome DevTools plugin** رسمی                         |

> **تصمیم:** انتخاب **MF 2.0** حدود **۳۰–۴۰٪ از کد زیرساختی سفارشی** (RemoteLoader، manifest، type-gen بین‌مرزی) را حذف می‌کند. runtime آن باندلر-اگنوستیک است، پس مهاجرت آینده‌ی Webpack → Rspack بدون تغییر منطق federation ممکن می‌ماند.
>
> **الگوهای بخش‌های ۱۸ (Resilience) و ۳/۲۰ (manifest)** به‌جای پیاده‌سازی از صفر، روی **hookها و API بومی MF 2.0** سوار می‌شوند (در همان بخش‌ها مشخص شده است).

#### تصمیم نسخه‌ی React: **۱۸** (ADR — پاسخ به m1)

اگر چه React 19 پایدار است، اما برای این پروژه **React 18** انتخاب می‌شود:

- اکوسیستم Module Federation، Design System و کتابخانه‌های جانبی روی ۱۸ بالغ‌تر و آزموده‌ترند.
- برای یک اپ داخل‌سازمانی، قابلیت‌های جدید ۱۹ (مثل Actions / `use`) مزیت تعیین‌کننده‌ای ندارند.
- ارتقا به ۱۹ در آینده یک **major bump هماهنگ‌شده** خواهد بود (چون React تنها قید سختِ singleton است — بخش ۱۶-۱). این تصمیم به‌صراحت به‌عنوان ADR ثبت می‌شود تا بعداً بازبینی شود.

**باندلر پیشنهادی / Bundler recommendation:**

| گزینه                            | توصیه              | دلیل                                                                                                        |
| -------------------------------- | ------------------ | ----------------------------------------------------------------------------------------------------------- |
| **Rspack**                       | 🟢 پیشنهاد اول     | سرعت بیلد بسیار بالا (Rust)، سازگار با Webpack ecosystem، پشتیبانی درجه‌یک از `@module-federation/enhanced` |
| Vite + `@module-federation/vite` | 🟡 جایگزین         | سرعت dev عالی؛ با MF 2.0 پلاگین رسمی‌تر و بالغ‌تری دارد                                                     |
| Webpack 5                        | 🟡 پشتیبانی می‌شود | پایدارترین، ولی کندترین در بیلد                                                                             |

> **توصیه (به‌روزشده):** چون runtimeِ **MF 2.0 باندلر-اگنوستیک** است، انتخاب باندلر یک تصمیم عملکردی است نه معماری. چون این پروژه **از صفر** شروع می‌شود، از **Rspack از فاز ۰** استفاده می‌کنیم: `@module-federation/enhanced/rspack` پشتیبانی رسمی دارد، runtime آن webpack-compatible است و بیلد به‌مراتب سریع‌تر است. تفاوت‌ها با Webpack فقط در سطح toolchain است (SWC بومی به‌جای `ts-loader`، CSS/CSS Modules بومی به‌جای `css-loader`/`mini-css-extract`، `HtmlRspackPlugin` به‌جای `html-webpack-plugin`) و هیچ‌کدام منطقِ federation را تغییر نمی‌دهند. در صورت نیاز، مهاجرت معکوس به Webpack هم بدون تغییر federation ممکن است.

**Orchestrator مونوریپو: Nx (از فاز ۰).** روی Rspack، لایه‌ی هماهنگیِ **Nx** اضافه می‌شود تا کش، اجرای موازی، `nx affected` (بیلد/تستِ فقط پروژه‌های متأثر) و **گراف وابستگی** را بدهد. برای Module Federation، Nx پلاگین‌های بومیِ Rspack دارد — `NxModuleFederationPlugin` و `NxModuleFederationDevServerPlugin` از `@nx/module-federation/rspack` — که **جایگزینِ رسمیِ** helper قدیمیِ `withModuleFederation` هستند و پیکربندی را از `module-federation.config.ts` می‌خوانند (زیرِ کاپوت همان `@module-federation/enhanced`). `nx serve shell` به‌صورت خودکار Remoteهای وابسته را کشف و سرو می‌کند. Nx با `bun` کار می‌کند؛ در صورت مشکل، `pnpm` جایگزینِ بالغ‌تر است.

---

### ۲-۲) ساختار ریپوها: Nx Monorepo (مسیر اصلی) — Polyrepo فقط در صورت نیاز

**تصمیم / Decision:** مسیرِ اصلی یک **Nx Monorepo** است و تا زمانی که الزامِ سازمانیِ واقعی به ACLِ سطحِ ریپو نباشد، **نمی‌شکنیم**:

- **همه‌ی فازها: Nx Monorepo با workspace** (`bun`/`pnpm`) — همه‌ی پکیج‌ها و اپ‌ها در یک ریپو، با **مرزبندیِ tag-محور** (`@nx/enforce-module-boundaries`).
- **split به Polyrepo فقط در صورت نیاز:** اگر روزی الزام شد تیم‌ها به‌کلی به سورسِ هم دسترسی نداشته باشند (ACL سطحِ ریپو)، هر package/app به ریپوی مستقل منتقل می‌شود. تا آن‌زمان، ACL/مرزبندی در همان مونوریپو حل است.

> 🔑 **چرا Nx Monorepo؟** معماریِ Module Federation در monorepo و polyrepo **دقیقاً یکسان** است (federation در runtime رخ می‌دهد، نه در ساختار ریپو). Monorepo دام «دو نسخه‌ی React» (M4) را با **dedup بومیِ workspace** حل می‌کند و **Nx** روی آن کش، `affected`، گراف وابستگی و **مرزبندیِ ماژول/ACL** را می‌دهد — یعنی تنها محرکِ Polyrepo (عدم‌دسترسیِ متقاطع تیم‌ها) بدون شکستنِ ریپو پوشش داده می‌شود.

**قواعد مرزبندی (تا split در صورت لزوم ارزان بماند):**

1. هر package/app یک `package.json` مستقل با **نامِ scoped** دارد (`@superapp/ui`, `remote-food`, ...).
2. وابستگی‌ها فقط از طریق **نامِ پکیج** import می‌شوند (`@superapp/ui`)، **نه** با مسیر نسبی (`../../shared/ui`).
3. federation config و اسکریپت‌ها به مسیرِ فیزیکیِ monorepo وابسته نباشند (Nx target/graph).
4. هر پروژه در `project.json` **تگ** می‌گیرد (`scope:*`, `type:*`) و `@nx/enforce-module-boundaries` importهای غیرمجاز بین scopeها را در CI رد می‌کند — همان ACLِ نرم که جای Polyrepو را می‌گیرد.

**ساختار Nx Monorepo:**

```
superapp/                         ← یک ریپوی Git، workspace root
├── package.json                  ← workspaces: ["shell", "remote-*", "shared/*"]
├── nx.json                       ← کش/گراف/پلاگین‌های Nx
├── bun.lockb
├── shell/                        ← @superapp/shell (Host)  → localhost:3000  (tags: scope:shell)
│   └── module-federation.config.ts
├── remote-food/                  ← remote-food             → localhost:3001  (tags: scope:food)
│   └── module-federation.config.ts
└── shared/
    ├── ui/                       ← @superapp/ui   (tags: type:ui)
    ├── auth/                     ← @superapp/auth
    ├── api/                      ← @superapp/api
    ├── state/                    ← @superapp/state
    ├── query/                    ← @superapp/query
    ├── types/                    ← @superapp/types
    └── foundation/               ← @superapp/foundation
```

**اشتراک پکیج:** از طریق **workspace** — تغییر در `@superapp/ui` بلافاصله در Shell/Remote دیده می‌شود (بدون publish، بدون Verdaccio). `nx affected` تضمین می‌کند فقط پروژه‌های متأثر بازبیلد شوند.

> ⚠️ **هشدار DX (رفع M4):** از `npm link`/`bun link` برای پکیج‌های دارای React **پرهیز کنید** (باعث بارگذاری نسخه‌ی دومِ React و خطای «Invalid hook call» می‌شود). در Monorepo این مشکل خودبه‌خود با **dedup بومیِ workspace** حل است. اگر روزی مجبور به link شدید، React را در `resolutions`/`overrides` روی یک نسخه pin کنید.

**ساختار شرکت — Azure DevOps (فقط اگر split به Polyrepo الزام شد):**

```
Azure DevOps Project: "SuperApp"
│
├── 📦 repos/                                  ← هر ریپو، دسترسی (ACL) مستقل
│   ├── 📁 shell/                              ← پوسته‌ی مرکزی (Host)
│   ├── 📁 remote-food/
│   ├── 📁 remote-account/
│   └── 📁 remote-.../
│
└── 📦 shared/                                 ← پکیج‌های مشترک، در Azure Artifacts
    ├── 📁 ui/          ← @superapp/ui
    ├── 📁 auth/        ← @superapp/auth
    ├── 📁 api/         ← @superapp/api
    └── 📁 template/    ← @superapp/template (scaffolder)
```

**چه زمانی به Polyrepo می‌رویم؟ (فقط در صورت نیاز)**

> محرک اصلی: **«تیم‌های مختلف نباید به سورس همه‌ی پروژه دسترسی داشته باشند»** (پاسخ تکمیلی مدیر تیم). اما در **Nx Monorepo** این با تگ‌ها + `@nx/enforce-module-boundaries` + `CODEOWNERS` (و در صورت نیاز، سیاست‌های شاخه) پوشش داده می‌شود. Polyrepو فقط وقتی لازم است که ACLِ **سطحِ ریپو** (جداییِ کاملِ سورس) الزام سازمانی باشد.

| معیار / Criterion             | Nx Monorepo (مسیر اصلی)                            | Polyrepo (فقط در صورت نیاز)           |
| ----------------------------- | -------------------------------------------------- | ------------------------------------- |
| کنترل دسترسیِ نرم (per-scope) | ✅ با `@nx/enforce-module-boundaries` + CODEOWNERS | ✅ بله                                |
| کنترل دسترسیِ سختِ سطحِ ریپو  | ❌ (همه سورس را می‌بینند)                          | ✅ تنها دلیلِ موجه برای split         |
| استقلال بیلد/دیپلوی           | ✅ Nx targets + `affected`                         | ✅ طبیعی                              |
| انتشار پکیج مشترک             | ✅ از طریق workspace (بدون publish)                | ✅ از طریق registry (Azure Artifacts) |
| سرعت DX                       | ✅ سریع (dedup + کش Nx)                            | ⚠️ کند (publish/install)              |
| هماهنگی نسخه‌ها               | ✅ خودکار                                          | ⚠️ نیاز به انضباط                     |

**استراتژی اشتراک کد / Code sharing strategy:**

- **PoC محلی (Monorepo):** پکیج‌های مشترک از طریق **workspace** مصرف می‌شوند — بدون publish، بدون Verdaccio، بدون `link`.
- **شرکت (Polyrepo):** پکیج‌های مشترک در **Azure Artifacts** به‌صورت **npm package با semver** منتشر می‌شوند (`bun`/`pnpm` هر دو با رجیستری npm سازگارند).
- هر Remote نسخه‌ی دلخواه خود را در `package.json` قید می‌کند و **نسخه‌بندی/rollback مستقل** دارد (اولویت — بخش ۱۶).
- قاعده‌ی نسخه‌بندی: **Semantic Versioning (semver)** سخت‌گیرانه — Breaking change → major.
- **انضباط سازگاری عقب‌رو:** پکیج‌های مشترک در طول یک major عقب‌رو سازگار می‌مانند تا rollback مستقل Remote ها ایمن باشد (بخش ۱۶-۵).

---

### ۲-۳) احراز هویت و نشست مشترک / Authentication & Shared Session

**تصمیم / Decision:** احراز هویت **یک‌بار** در Shell انجام می‌شود و Remote ها از طریق یک **Auth SDK مشترک** به توکن دسترسی دارند.

```
┌──────────────────────────────────────────────────────────────┐
│                      Browser (Client)                        │
│                                                              │
│   ┌───────────────────────────────────────────────────────┐  │
│   │                    SHELL (Host)                       │  │
│   │                                                       │  │
│   │   ┌─────────────┐   ┌──────────────┐  ┌────────────┐  │  │
│   │   │ Login (OIDC)│──▶│ Token Store │─▶│AuthContext │  │  │
│   │   │             │   │ (JWT access  │  │ (Provider) │  │  │
│   │   │             │   │  + refresh)  │  │            │  │  │
│   │   └─────────────┘   └──────────────┘  └─────┬──────┘  │  │
│   │                                            │          │  │
│   │   ┌─────────────┐  ┌─────────────┐         │          │  │
│   │   │ Remote:Food │  │Remote:Acct  │◀───────┘│          │  │
│   │   │     │       │  │     │       │  (useAuth hook)    │  │
│   │   └──────┼──────┘  └──────┼──────┘                    │  │
│   └──────────┼────────────────┼───────────────────────────┘  │
│              │                │                              │
└──────────────┼────────────────┼──────────────────────────────┘
               ▼                ▼
        ┌─────────────┐  ┌─────────────┐
        │ Food API    │  │ Account API │   ← میکروسرویس‌ها
        │ (audience:  │  │ (audience:  │     با همان JWT
        │   "food")   │  │  "account") │
        └─────────────┘  └─────────────┘
```

**نکات کلیدی / Key points:**

| موضوع                            | تصمیم                                                                                                                                                                                                                                     |
| -------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| OIDC flow                        | **Authorization Code Flow با PKCE** (توصیه‌شده برای SPA)                                                                                                                                                                                  |
| ذخیره‌ی توکن                     | access token کوتاه‌مدت (۱۵ دقیقه) در حافظه (memory)؛ refresh token در **httpOnly secure cookie** (یا امن‌ترین گزینه‌ی ممکن)                                                                                                               |
| **محافظت CSRF** (m4)             | چون refresh از طریق cookie انجام می‌شود، endpoint refresh باید در برابر CSRF محافظت شود: کوکی با **`SameSite=Strict`** (یا حداقل `Lax`) + **anti-CSRF token** (double-submit) برای درخواست refresh. این در `@superapp/auth` پیاده می‌شود. |
| اشتراک به Remote                 | Remote از طریق `useAuth()` hook در `@superapp/auth` به توکن می‌رسد — بدون دسترسی مستقیم به storage                                                                                                                                        |
| Refresh / انقضا                  | Shell مسئول refresh خودکار؛ Remote ها از طریق **Auth Events** مطلع می‌شوند                                                                                                                                                                |
| Reload صفحه                      | با هر full reload، access token در memory پاک می‌شود؛ Shell با **silent renew** (استفاده از refresh cookie) نشست را بازیابی می‌کند — چون کار آفلاین لازم نیست، این جریان همیشه به شبکه دسترسی دارد و ساده است                             |
| **Deep-link با نشست منقضی** (G6) | ورود مستقیم به `/food/orders/123` وقتی refresh منقضی است: Shell **URL مقصد را نگه می‌دارد** (`returnUrl`)، به `/login` هدایت می‌کند، و پس از لاگین موفق به همان deep-link بازمی‌گرداند. این جریان در Shell (نه Remote) متمرکز است.        |
| Audience / Scope                 | توکن واحد با **audience مشترک** (Q1) — همه‌ی سرویس‌ها همون توکن را قبول می‌کنند                                                                                                                                                           |

> 💡 **PoC محلی (فاز ۰–۲):** به‌جای OIDC واقعی، `@superapp/auth` با **mock login** (JWT ساختگی در memory) پیاده می‌شود. API `@superapp/auth` یکسان می‌ماند تا در **فاز ۳** فقط backend adapter به Identity Server شرکت عوض شود.

**Auth Events استاندارد / Standard Auth Events:**

`@superapp/auth` یک **event bus** داخلی دارد که این رویدادها را پخش می‌کند تا Remote ها بتوانند واکنش نشان دهند:

| Event                  | زمان انتشار / When emitted                 | مصرف‌کننده‌ی رایج / Typical consumer       |
| ---------------------- | ------------------------------------------ | ------------------------------------------ |
| `auth:logged_in`       | کاربر با موفقیت لاگین کرد                  | Remote: بارگذاری داده‌ی اولیه              |
| `auth:token_refreshed` | توکن جدید صادر شد (refresh موفق)           | Remote: هیچ (شفاف)                         |
| `auth:token_expiring`  | توکن در آستانه‌ی انقضا (مثلاً ۵ دقیقه قبل) | Remote: درخواست‌های در حال اجرا را کامل کن |
| `auth:token_expired`   | توکن منقضی شد و refresh ناموفق بود         | Remote: پاک‌سازی داده‌ی حساس               |
| `auth:logout`          | کاربر لاگ‌اوت کرد                          | Remote: پاک‌سازی کامل state محلی           |
| `auth:session_changed` | نشست تغییر کرد (مثلاً ورود از دستگاه دیگر) | Remote: reload                             |

```ts
// در هر Remote:
import { authEvents } from "@superapp/auth";

authEvents.on("auth:logout", () => {
  useLocalStore.getState().clearSensitiveData();
});
```

> ⚠️ **نکته‌ی امنیتی (Q1):** چون توکن واحد با audience مشترک است، لو رفتن آن = دسترسی به همه‌ی سرویس‌ها. به‌خاطر همین، توکن **کوتاه‌مدت (۱۵ دقیقه)** و `@superapp/auth` طوری طراحی می‌شود که در صورت مهاجرت آینده به multi-audience، فقط این پکیج آپدیت شود.

---

### ۲-۴) مدیریت State مشترک / Shared State Management

**تصمیم / Decision:** **Shell مالک state سراسری** با **Zustand**؛ Remote ها فقط مصرف‌کننده (consumer) هستند.

| لایه‌ی state                       | مالک / Owner             | ابزار / Tool                                 | مثال                                               |
| ---------------------------------- | ------------------------ | -------------------------------------------- | -------------------------------------------------- |
| سراسری (global)                    | **Shell**                | **Zustand** (singleton)                      | کاربر جاری، دسترسی‌ها، تنظیمات نوار بالا، زبان، تم |
| **server-state** (داده‌ی سمت سرور) | **هر Remote** (کش مشترک) | **TanStack Query** به‌صورت singleton (بخش ۸) | داده‌ی API، cache، dedup، background refetch       |
| محلی (local / UI)                  | **هر Remote**            | آزاد (Zustand یا `useState`)                 | فرم‌ها، حالت باز/بسته، داده‌ی داخل صفحه            |

> 📌 **تفکیک مهم (رفع M2):** «client-state» (Zustand) و «server-state» (TanStack Query) دو نگرانی جدا هستند. Zustand برای state سراسری UI است و **نباید** برای کش داده‌ی API استفاده شود. مدیریت داده‌ی سرور (caching, dedup, invalidation, background refetch) در **بخش ۸** با `@superapp/query` تعریف شده است.

**چرا Zustand؟**

- ✅ سبک، بدون boilerplate، API ساده
- ✅ بدون Provider wrapper (برخلاف Context) → مناسب cross-boundary در federation
- ✅ selective subscription با selector → جلوی re-render اضافی را می‌گیرد
- ✅ می‌توان آن را به‌عنوان singleton بین Shell و Remote ها به اشتراک گذاشت (critical برای federation)

**الگوی دسترسی Remote ها به state سراسری:**

- Shell یک **Zustand store** سراسری (در `@superapp/state`) در ریشه ایجاد می‌کند.
- Remote از طریق hook های موجود در `@superapp/state` به آن دسترسی پیدا می‌کند — نه با import مستقیم از Shell.

```ts
// @superapp/state — Zustand store سراسری (singleton)
export const useGlobalStore = create<GlobalState>((set, get) => ({
  user: null,
  permissions: [],
  // ...
}));

// در هر Remote:
import { useGlobalStore } from "@superapp/state";
const user = useGlobalStore((s) => s.user); // selective subscription
```

- برای ارتباط Remote ↔ Remote (در صورت نیاز): **Event Bus تایپ‌امن** یا store مشترک با namespace.

> قاعده: **Remote ها نباید state یکدیگر را مستقیماً تغییر دهند.** ارتباط فقط از طریق Shell یا event.

---

### ۲-۵) مسیریابی / Routing

**تصمیم / Decision:** مسیریابی یکپارچه در **Shell** با نقاط اتصال (mount points) برای Remote ها.

| مسیر / Route        | بارگذاری / Loads                         |
| ------------------- | ---------------------------------------- |
| `/`                 | صفحه‌ی اصلی Shell (داشبورد/انتخاب ماژول) |
| `/food/*`           | Remote:Food                              |
| `/account/*`        | Remote:Account                           |
| `/login`, `/logout` | صفحه‌های احراز هویت Shell                |

- Shell از `react-router-dom` استفاده می‌کند و Remote را در `<Route path="/food/*">` بارگذاری می‌کند.
- هر Remote می‌تواند **sub-routing** داخلی خود را داشته باشد (`/food/orders/:id`).
- ⚠️ **`react-router-dom` باید shared singleton باشد** (در federation config — بخش ۱۶-۱ و ۱۸-۴). در غیر این صورت Router context بین Shell و Remote یکی نمی‌شود و `useNavigate`/`<Link>`/`useParams`ِ Remote یا کار نمی‌کند یا یک Routerِ دومِ مستقل می‌سازد (باگ رایج و پرهزینه‌ی MF). نسخه‌ی آن در قاعده‌ی کف/سقف بخش ۱۶ لحاظ شده است.
- **هماهنگیِ منو ↔ Remote (رفع G4):** لیست مسیرهای مجاز از RBAC (API/Identity) می‌آید، اما آدرس فیزیکیِ هر Remote از **manifest** خوانده می‌شود. اگر RBAC مسیری را مجاز کند که در manifest نباشد (یا برعکس)، Shell آن آیتم منو را **غیرفعال/پنهان** می‌کند و خطا را لاگ می‌کند — نه صفحه‌ی خطا. یعنی «تقاطعِ RBAC ∩ manifest» تعیین‌کننده‌ی منوی نهایی است.

---

## ۳) دیاگرام معماری / Architecture Diagram

```
                              ┌──────────────────────────┐
                              │      Identity Server     │
                              │      (OIDC / OAuth2)     │
                              └────────────┬─────────────┘
                                           │ JWT
                                           ▼
┌────────────────────────────────────────────────────────────────────────────┐
│                        BROWSER (Single Page App)                           │
│                                                                            │
│  ┌──────────────────────────────────────────────────────────────────────┐  │
│  │                         SHELL (Host)                                 │  │
│  │                                                                      │  │
│  │ Auth │ Routing │ Global State │ Menu (RBAC) │ Layout │ Design System │  │
│  │                                                                      │  │
│  │   ┌──────────────┬──────────────┬──────────────┬───────────┐         │  │
│  │   │ Remote:Food  │Remote:Account│ Remote:...   │ Remote:.. │         │  │
│  │   │ (from CDN/   │ (from CDN/   │              │           │         │  │
│  │   │  App Service)│  App Service)│              │           │         │  │
│  │   └──────┬───────┴──────┬───────┴──────────────┴───────────┘         │  │
│  └──────────┼──────────────┼────────────────────────────────────────────┘  │
└─────────────┼──────────────┼───────────────────────────────────────────────┘
              │              │
              ▼              ▼
   ┌─────────────────────────────────┐
   │        API Gateway / BFF        │   (اختیاری / optional)
   │   (routing, aggregation, CORS)  │
   └──────────────┬──────────────────┘
                  │
      ┌───────────┼───────────┬─────────────┐
      ▼           ▼           ▼             ▼
┌──────────┐ ┌───────────┐ ┌──────────┐ ┌──────────┐
│ Food API │ │Account API│ │  ... API │ │  ... API │   ← Microservices
└──────────┘ └───────────┘ └──────────┘ └──────────┘
```

### منبع Remote ها از کجا بارگذاری می‌شوند؟ / Where are Remotes served from?

هر Remote به‌صورت **static build** (فایل‌های `remoteEntry.js` و asset ها) در یک endpoint مستقل سرو می‌شود:

| محیط                   | Shell                     | Remote:Food                                           |
| ---------------------- | ------------------------- | ----------------------------------------------------- |
| **PoC محلی (فاز ۰–۲)** | `http://localhost:3000`   | `http://localhost:3001/remoteEntry.js`                |
| **شرکت (هدف نهایی)**   | `https://app.company.com` | `https://app.company.com/remotes/food/remoteEntry.js` |

> **PoC محلی:** Shell در federation config مستقیماً به `localhost:3001` اشاره می‌کند — نیازی به manifest سرور یا HTTPS نیست.
>
> **شرکت:** همه‌ی Remote ها زیر **همان دامنه‌ی Shell** سرو شوند تا CORS و کوکی auth ساده بماند. آدرس Remote ها در **manifest مرکزی** (`mf-manifest.json` بومی MF 2.0) نگهداری می‌شود.

> ⚠️ **رفتار Shell هنگام در دسترس نبودن manifest (رفع m6 — نقطه‌ی شکست واحد):**
>
> - **کش با نسخه:** آخرین manifest سالم در Shell کش می‌شود؛ اگر دریافت manifest جدید شکست خورد، Shell از **نسخه‌ی کش‌شده‌ی قبلی** استفاده می‌کند (fail-safe) و خطا را گزارش می‌دهد.
> - **سرو ایستا و افزونه:** manifest به‌صورت فایل استاتیک با هدرهای کش کوتاه و در صورت امکان از **دو مبدأ** (CDN + origin) سرو شود.
> - **degradation کنترل‌شده:** اگر هیچ manifest در دسترس نبود، Shell خودش بالا می‌آید و به‌جای صفحه‌ی سفید، خطای «بارگذاری برنامه‌ها ناموفق بود — تلاش مجدد» نشان می‌دهد (سازگار با Error Boundary سراسری — بخش ۱۸-۵).

---

## ۴) ساختار پکیج‌های مشترک / Shared Packages

| پکیج / Package       | وظیفه / Responsibility                                                                                                         | مصرف‌کننده / Consumers               |
| -------------------- | ------------------------------------------------------------------------------------------------------------------------------ | ------------------------------------ |
| `@superapp/ui`       | **Design System (RTL-first)** — کامپوننت‌ها، توکن‌ها (رنگ، فاصله، تایپوگرافی)، logical properties، CSS isolation (بخش ۷، ۱۱-۸) | Shell + همه‌ی Remote ها              |
| `@superapp/auth`     | Auth SDK — OIDC client، `useAuth()` hook، refresh خودکار + CSRF، Audience handling                                             | Shell (owner) + Remote ها (consumer) |
| `@superapp/api`      | API utils — HTTP client (fetch/axios wrapper)، interceptor توکن، error handling                                                | همه‌ی Remote ها                      |
| `@superapp/query` 🆕 | **Server-state** — پیکربندی مشترک TanStack Query (singleton)، کلیدهای کش، invalidation، دیفالت‌های refetch (بخش ۸)             | همه‌ی Remote ها                      |
| `@superapp/state`    | state سراسری مشترک — types و hooks برای خواندن state Shell (client-state)                                                      | Remote ها                            |
| `@superapp/template` | **CLI scaffolder** — ساخت Remote جدید در چند دقیقه (شامل federation config، routing، auth، query، adaptive)                    | برای ایجاد Remote جدید               |
| `@superapp/types`    | انواع مشترک (User, Permissions, Menu)                                                                                          | همه‌جا                               |

> 📌 **سیاست shared-deps / dedup (رفع m5):** علاوه بر `react`/`react-dom`/`zustand`/`@superapp/*`، کتابخانه‌های سنگینِ **مشترک بین چند Remote** (مثلاً `@tanstack/react-query`، کتابخانه‌ی آیکون، کتابخانه‌ی date مثل `date-fns`/`dayjs`) نیز باید در `shared` قید شوند تا در هر Remote تکرار نشوند (جلوگیری از bundle bloat). قاعده: **هر dep که در ≥۲ Remote استفاده می‌شود و سنگین است، کاندیدای `shared` است.** جزئیات و بودجه در بخش ۲۰.

### چرخه‌ی انتشار / Release workflow

**PoC محلی (فاز ۰–۲):** تغییر در `@superapp/ui` بلافاصله از طریق **workspace** در Shell/Remote دیده می‌شود (بدون publish، بدون Verdaccio).

**شرکت (فاز ۳+):**

```
   developer commits to `shared/design-system`
            │
            ▼
   ┌────────────────────┐
   │  Azure Pipeline    │  ← بیلد + publish
   │  (shared-*)        │
   └─────────┬──────────┘
             │ pass
             ▼
   ┌────────────────────┐
   │  bump version      │  ← semver (patch/minor/major)
   │  (auto or manual)  │
   └─────────┬──────────┘
             ▼
   ┌────────────────────┐
   │ Azure Artifacts    │  ← @superapp/ui@1.4.2
   │ (npm registry)     │
   └────────────────────┘
             │
             ▼
   Remote ها با `bun update @superapp/ui` ارتقا می‌دهند (تدریجی)
```

---

## ۵) CI/CD / DevOps Pipeline

> **دو فاز مجزا:** در **فاز ۰–۲ (PoC محلی)** هیچ Azure Pipeline یا Artifacts لازم نیست — فقط `bun run dev` / `bun run build` روی سیستم شخصی. **Azure DevOps از فاز ۳ (زیرساخت شرکت)** وارد می‌شود.

### PoC محلی (فاز ۰–۲): بدون Azure

| کار             | دستور / روش                                            | خروجی                              |
| --------------- | ------------------------------------------------------ | ---------------------------------- |
| توسعه           | `nx serve shell` (Remoteهای وابسته خودکار سرو می‌شوند) | Hot reload روی localhost           |
| بیلد production | `bun run build` در هر پکیج                             | `dist/` + `remoteEntry.js`         |
| تست یکپارچگی    | `bun run preview` یا serve استاتیک محلی                | اعتبارسنجی federation بعد از build |
| اشتراک پکیج     | **workspace** (`bun install` — بدون publish)           | dedup بومی، بدون registry          |

```
┌─────────────────────────────────────────────────────────────┐
│  Nx Monorepo محلی (یک repo)                                 │
│                                                             │
│  nx serve shell ──▶ Shell :3000 ──loads──▶ Remote :3001    │
│                                                             │
│  nx run-many -t build ──▶ dist/ ──▶ preview ──▶ تست دستی  │
└─────────────────────────────────────────────────────────────┘
```

### شرکت (فاز ۳+): Azure Pipeline — Build → Deploy

پس از استقرار PoC روی زیرساخت شرکت، Pipeline ساده **Build → Deploy** راه‌اندازی می‌شود. lint، تست، smoke test و approval gate **مرحله‌به‌مرحله بعداً** اضافه می‌شوند.

| نوع پکیج / Component | Trigger        | خروجی / Output                                      |
| -------------------- | -------------- | --------------------------------------------------- |
| `shared/*` (پکیج‌ها) | push به `main` | بیلد → انتشار در **Azure Artifacts**                |
| `shell`              | push به `main` | بیلد → دیپلوی به App Service / Static Site          |
| `remote-*`           | push به `main` | بیلد federation → دیپلوی **مستقل** به endpoint خودش |

```
┌─────────────────────────────────────────────────────────────┐
│  remote-food repo (Azure Repos)                             │
│                                                             │
│  push به main ──▶┌──────────────┐ ──▶ ┌─────────────────┐  │
│                   │  Build       │     │  Deploy          │ │
│                   │  (federation │     │  /remotes/food/  │ │
│                   │  remoteEntry)│     │  (independent)   │ │
│                   └──────────────┘     └──────────────────┘ │
└─────────────────────────────────────────────────────────────┘
```

**نکات کلیدی (شرکت):**

- **هر Remote، pipeline مستقل دارد** ← همزمانی تیم‌ها، rollback سریع (سوال ۱۳).
- بیلد federation: فقط `remoteEntry.js` و chunk های آن re-deploy می‌شوند (سوال ۱۲).
- **Manifest مرکزی:** rollback = تغییر ورودی manifest.

### تکمیل Pipeline (فاز ۴ و بعد) / Full Pipeline — Later

| مرحله                                                     | زمان پیشنهادی        |
| --------------------------------------------------------- | -------------------- |
| Lint + Type Check                                         | فاز ۴                |
| Unit / Component Test                                     | فاز ۴                |
| Bundle Budget                                             | فاز ۴                |
| Smoke Test پس از deploy                                   | فاز ۴                |
| Environments (`dev` → `staging` → `prod`) + approval gate | فاز ۴                |
| Canary / Blue-Green (بخش ۲۳)                              | پس از استقرار پایدار |

> 📌 جزئیات Pipeline کامل در **بخش ۱۹-۴** و **بخش ۲۳** — نقشه‌ی راه آینده در شرکت، نه الزام PoC محلی.

---

## ۶) فازبندی MVP / MVP Phasing (هدف ~۲ ماه، منعطف)

> **قاعده‌ی طلایی:** MVP فقط **Shell + یک Remote نمونه** است. **فاز ۰–۲ روی سیستم شخصی** (بدون Azure) پیاده و تست می‌شود. **فاز ۳** = استقرار روی زیرساخت شرکت و راه‌اندازی Azure. **فاز ۴** = تکمیل تدریجی در محیط شرکت.

> ⏱️ **درباره‌ی زمان‌بندی:** تخمین «~۲ ماه» یک **هدف برنامه‌ریزی** است، نه ضرب‌الاجل قراردادی. اگر اجرا کمی طولانی‌تر شود مشکلی ایجاد نمی‌کند؛ **اولویت با کیفیت و اثبات درست معماری است**، نه فشرده‌سازی زمان. بازه‌های هفتگی زیر صرفاً برای ترتیب و توالی کارها هستند و می‌توانند کش بیایند. با این حال، برای کاهش ریسک، بهتر است اتوماسیون سنگین (ابزار PR خودکار `foundation` و scaffolder کامل `template`) در صورت فشار زمانی به **فاز ۳** موکول شوند و هدف اصلی PoC روی **«federation + auth + state + یک Remote adaptive»** متمرکز بماند (رفع M5).

### نمای کلی مسیر

```
  تأیید سند معماری
         │
         ▼
  ┌──────────────────┐     فاز ۰–۲ (PoC محلی)     ┌──────────────────┐
  │  Nx Monorepo     │  Shell + Remote + mock      │  PoC قابل نمایش │
  │  محلی (یک repo)  │  ────────────────────────▶ │  تست دستی OK    │
  └──────────────────┘                             └────────┬─────────┘
                                                            │
                                                            ▼
                                                    ┌──────────────────┐
                                                    │  فاز ۳: استقرار │
                                                    │  Azure + OIDC    │
                                                    │  واقعی + Deploy  │
                                                    └────────┬─────────┘
                                                             │
                                                             ▼
                                                    ┌──────────────────┐
                                                    │  فاز ۴: تکمیل   │
                                                    │  Pipeline، TWA،  │
                                                    │  monitoring، ... │
                                                    └──────────────────┘
```

### فاز ۰: Foundation (هفته ۱–۲) — **محلی**

| کار / Task                                                                                                                                                                                              | خروجی / Deliverable                          |
| ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------- |
| ساخت **Nx Monorepo** — `bunx nx init` روی یک ریپو با `shell/`، `remote-food/`، `shared/*`؛ تگ‌گذاریِ scope و `@nx/enforce-module-boundaries` (بخش ۲-۲)                                                  | ساختار PoC + مرزبندی                         |
| پیکربندی **`bun` workspace + Nx** (dedup بومی React؛ کش/گراف Nx؛ بدون Verdaccio، بدون `link`)                                                                                                           | نصب/اشتراک پکیج‌های shared                   |
| 🆕 ساخت **`@superapp/foundation`** (در PoC: فقط `canonical.json` + `compatibility.json` + `max-in-production.json` دستی — بدون ابزار PR خودکار)                                                         | مجموعه‌ی نسخه‌ی canonical توصیه‌شده (بخش ۱۶) |
| قالب Shell با MF 2.0 روی Rspack (`NxModuleFederationPlugin` + `module-federation.config.ts`؛ `shared` شامل `react`, `react-dom`, `react-router-dom`, `@tanstack/react-query`, `zustand`, `@superapp/*`) | Shell بوت‌استرپ                              |
| قالب Remote (در PoC: `nx g @nx/react:remote` یا کپیِ دایرکتوری؛ scaffolder کامل `@superapp/template` می‌تواند فاز ۳ باشد)                                                                               | قالب Remote                                  |
| پیکربندی **Tailwind CSS v4** + `cva` + `clsx`/`tailwind-merge` + توکن‌های `@theme`؛ **تمِ مشترک در `@superapp/ui`** و **preflight فقط در Shell** (بخش ۷)                                                | استایل ایزوله از روز اول                     |
| اجرای `nx serve shell` (Remoteهای وابسته خودکار سرو می‌شوند)                                                                                                                                            | DX محلی                                      |

> ❌ **در این فاز انجام نمی‌شود:** Azure DevOps، Artifacts، Pipeline، Verdaccio، split ریپوها. (ACL تیم‌ها با تگ‌های Nx در همان مونوریپو پوشش داده می‌شود.)

### فاز ۱: Core (هفته ۳–۵) — **محلی**

| کار / Task                                                                                                                               | خروجی / Deliverable            |
| ---------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------ |
| **Mock auth** در Shell (`@superapp/auth` با JWT ساختگی / json-server) — نه OIDC واقعی                                                    | لاگین/لاگ‌اوت، token store     |
| مسیریابی + mount point برای Remote                                                                                                       | Shell آماده‌ی بارگذاری Remote  |
| Auth Context + `useAuth()`                                                                                                               | اشتراک توکن با Remote          |
| **Mock API** محلی (MSW / json-server) به‌جای Gateway شرکت                                                                                | Remote با داده‌ی نمونه کار کند |
| Design System اولیه **RTL-first** (Theme + logical properties + ۱۰ کامپوننت پایه + `useViewport` + استراتژی CSS isolation — بخش ۷، ۱۱-۸) | `@superapp/ui` v1              |
| 🆕 **Storybook برای `@superapp/ui`** (`nx g @nx/react:storybook-configuration`؛ رندر در RTL/LTR از toolbar) — بخش ۱۰-۲                   | توسعه‌ی ایزوله‌ی کامپوننت      |
| 🆕 پیکربندی `@superapp/query` (TanStack Query singleton) + یک نمونه‌ی fetch در Remote (بخش ۸)                                            | server-state مشترک             |
| تست دستی Shell + Remote روی localhost (شامل بررسی چیدمان RTL)                                                                            | یکپارچگی federation            |

> ❌ **در این فاز انجام نمی‌شود:** Identity Server شرکت، API Gateway، Dev Shell سرور مشترک، Service Worker (→ فاز ۲ یا ۳)، Azure.

### فاز ۲: First Remote (هفته ۶–۸) — **محلی**

| کار / Task                                                      | خروجی / Deliverable         |
| --------------------------------------------------------------- | --------------------------- |
| ساخت Remote نمونه (مثلاً «حملونقل/غذا») با `@superapp/template` | Remote اول                  |
| اتصال به mock API، استفاده از `@superapp/auth` + `@superapp/ui` | Remote کارکردنی             |
| 🆕 **Adaptive Components** (variants موبایل/دسکتاپ — بخش ۱۱)    | پشتیبانی دو viewport        |
| `bun run build` + serve/preview محلی                            | اعتبارسنجی production build |
| منوی پویا + RBAC (با mock permissions)                          | منو بر اساس دسترسی          |
| تست end-to-end دستی (Shell + Remote، موبایل + دسکتاپ)           | **PoC قابل ارائه**          |

### فاز ۳: استقرار روی زیرساخت شرکت — **Azure + استقرار مرحله‌ای**

> پس از تأیید PoC محلی، کد **مرحله‌به‌مرحله** روی زیرساخت واقعی شرکت مستقر و به سرویس‌های واقعی وصل می‌شود.

| مرحله | کار                                                                                                                                                          | خروجی                                     |
| ----- | ------------------------------------------------------------------------------------------------------------------------------------------------------------ | ----------------------------------------- |
| ۳-۱   | push ریپوی **Nx Monorepo** به **Azure DevOps** (Repos) + `CODEOWNERS`. **split به Polyrepo فقط اگر** ACL سطحِ ریپو الزام شد (با `git filter-repo`/`subtree`) | ریپوی شرکت روی Azure                      |
| ۳-۲   | (فقط در حالتِ Polyrepo) جایگزینی workspace با **Azure Artifacts** به‌عنوان registry (تنظیم `.npmrc` / `bunfig.toml`)                                         | registry شرکت                             |
| ۳-۳   | Pipeline ساده **Build → Deploy** برای Shell و Remote                                                                                                         | دیپلوی خودکار                             |
| ۳-۴   | اتصال **OIDC واقعی** (Identity Server شرکت)                                                                                                                  | auth production-ready                     |
| ۳-۵   | اتصال **API Gateway** و میکروسرویس‌ها                                                                                                                        | جایگزینی mock API                         |
| ۳-۶   | **Dev Shell** روی سرور شرکت + override (بخش ۱۰)                                                                                                              | DX تیم‌های Remote                         |
| ۳-۷   | Service Worker پایه (Workbox) + آماده‌سازی زیرساخت push (VAPID/FCM)                                                                                          | SW برای precache و push — **بدون آفلاین** |

### فاز ۴ (پس از استقرار): تکمیل و مقیاس

- 🆕 **تکمیل Pipeline:** lint، type check، unit/component test، bundle budget، smoke test، environments (بخش ۵).
- اضافه‌شدن Remote های بعدی با template.
- 🆕 فعال‌سازی Push Notification (FCM) — بخش ۱۷ (بدون Background Sync/آفلاین).
- 🆕 **TWA** و Google Play (بخش ۱۱).
- Application Insights و monitoring (بخش ۲۴).
- بهینه‌سازی بیلد Rspack (persistent cache، module federation runtime plugins).

---

## ۷) استراتژی استایل‌دهی / Styling Strategy (Tailwind-first)

> 🎯 **مشکل (رفع M1):** Module Federation فضای نام CSS **سراسری مشترک** دارد. اگر هر Remote رویکرد CSS متفاوت یا پیکربندیِ Tailwind متفاوتی داشته باشد، **نشت/تصادم استایل** بین Remote ها رخ می‌دهد (به‌ویژه از سمت **preflight/reset** سراسری). این با یک **تمِ مشترک (`@theme`) + قواعد ایزوله‌سازی** حل می‌شود، نه با ممنوعیت Tailwind.

### ۷-۱) تصمیم: پشته‌ی استایل‌دهی (از فاز ۰)

| لایه                   | ابزار                                                                                         | نقش                                                                                               |
| ---------------------- | --------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------- |
| **Styling**            | **Tailwind CSS**                                                                              | رویکرد اصلی — utility-first از روز اول                                                            |
| **Component Variants** | **`cva`** (class-variance-authority)                                                          | تعریف variantهای کامپوننت (size/intent/…) به‌صورت تایپ‌امن                                        |
| **Merge کلاس‌ها**      | **`clsx`** + **`tailwind-merge`** (ترکیب: `cn()`)                                             | ادغام شرطیِ کلاس‌ها و حلِ تعارضِ utilityهای Tailwind                                              |
| **Design Tokens**      | **`@theme` (CSS Variables)** — Tailwind v4                                                    | منبعِ واحدِ رنگ/فاصله/تایپوگرافی؛ `@theme` خودکار متغیرهای `var(--*)` و utilityها را تولید می‌کند |
| **Global CSS**         | فقط **reset، فونت‌ها، متغیرها، استایل‌های پایه**                                              | یک فایل سراسری، **فقط در Shell** بارگذاری می‌شود                                                  |
| **CSS Modules**        | فقط **موارد خاص**: انیمیشن‌های پیچیده، `@keyframes`، `clip-path`، `mask`، انتخابگرهای پیشرفته | استثنا، نه قاعده                                                                                  |

```ts
// @superapp/ui — helper ادغام کلاس‌ها (الگوی استاندارد پروژه)
import { clsx, type ClassValue } from "clsx";
import { twMerge } from "tailwind-merge";
export const cn = (...inputs: ClassValue[]) => twMerge(clsx(inputs));

// نمونه‌ی variant با cva:
import { cva, type VariantProps } from "class-variance-authority";
export const buttonVariants = cva("sa-btn", {
  variants: {
    intent: {
      primary: "bg-[var(--sa-color-primary)] text-white",
      ghost: "bg-transparent",
    },
    size: { sm: "px-2 py-1 text-sm", md: "px-4 py-2" },
  },
  defaultVariants: { intent: "primary", size: "md" },
});
```

### ۷-۲) ایزوله‌سازیِ Tailwind در Module Federation (رفع نگرانیِ نشت استایل)

سه قاعده‌ی الزامیِ فنی که Tailwind را در MF ایمن می‌کند:

| قاعده                                                                                                                                                                     | چرا                                                                                                   |
| ------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------- |
| **تمِ مشترک واحد با `@theme`** در یک فایل CSS در `@superapp/ui` که Shell و همه‌ی Remoteها آن را `@import` می‌کنند (Tailwind v4 مبتنی بر CSS است، نه `tailwind.config.js`) | تضمینِ یکسان بودنِ معنای utilityها و توکن‌ها بین همه؛ جلوگیری از drift                                |
| **preflight فقط یک‌بار (در Shell)** — Shell کلِ `@import "tailwindcss"` را می‌آورد؛ Remoteها فقط لایه‌های `theme` و `utilities` را import می‌کنند (بدونِ `preflight.css`) | preflight یک reset سراسری است؛ اگر هر Remote آن را دوباره تزریق کند، استایل‌های سراسری با هم می‌جنگند |
| **توکن‌ها با `@theme` (CSS Variables)** — رنگ/فونت/شعاع در `@theme` تعریف و به‌صورت `var(--*)` تولید می‌شوند                                                              | تغییر تم/توکن در یک‌جا (`@superapp/ui`) روی همه اثر می‌گذارد؛ بدونِ دوباره‌کاری                       |

- **`@superapp/template`** هر Remote جدید را با import تمِ مشترک + بدونِ `preflight` + helper `cn()` می‌سازد.
- **کامپوننت‌های تعاملی** در `@superapp/ui` با `cva` تعریف می‌شوند تا Remoteها utilityهای خام و ناسازگار ننویسند.
- **CSS Modules** فقط برای موارد خاص (بالا)؛ نامِ کلاس‌ها به‌صورت خودکار hash می‌شوند و تصادم ندارند.
- **Stylelint/ESLint** (فاز ۴): منعِ رنگ/فاصله‌ی hard-coded (به‌جای آن توکن)، و قاعده‌ی `tailwindcss/no-contradicting-classname`.

> 📌 **سازگاری با CSP (بخش ۲۰):** Tailwind به یک **فایل CSS استاتیک** کامپایل می‌شود (نه inline)، پس نیازی به `style-src 'unsafe-inline'` ندارد و با **CSP مبتنی بر hash** سازگار است. CSS-in-JS همچنان **ممنوع** است (چون به `unsafe-inline` نیاز دارد).

---

## ۸) مدیریت Server-State / Server-State Management (`@superapp/query`)

> 🎯 **مشکل (رفع M2):** `@superapp/api` فقط یک HTTP wrapper است. بدون یک لایه‌ی **کش داده‌ی سمت سرور**، هر Remote منطق fetch/cache/dedup/refetch خودش را دوباره اختراع می‌کند → کد پراکنده، ناسازگار و مستعد باگ (double-fetch، داده‌ی کهنه).

### ۸-۱) تصمیم: TanStack Query به‌صورت singleton مشترک

**تصمیم / Decision:** یک `QueryClient` **واحد** در Shell ساخته می‌شود و از طریق `@superapp/query` به‌صورت **singleton در federation** بین Shell و همه‌ی Remote ها به اشتراک گذاشته می‌شود (دقیقاً مثل Zustand).

| موضوع                | تصمیم                                                                              |
| -------------------- | ---------------------------------------------------------------------------------- |
| کتابخانه             | **TanStack Query (React Query)** — استاندارد de-facto server-state                 |
| مالکیت `QueryClient` | **Shell** می‌سازد؛ Remote ها مصرف می‌کنند (تا کش بین Remote ها مشترک باشد)         |
| اشتراک در federation | `@tanstack/react-query` در `shared` با `singleton: true` (بخش ۱۶، ۲۰)              |
| تفکیک از Zustand     | Zustand = client/UI-state؛ TanStack Query = server-state (کش، dedup، invalidation) |

```ts
// @superapp/query — QueryClient مشترک (singleton)
export const queryClient = new QueryClient({
  defaultOptions: {
    queries: { staleTime: 30_000, retry: 2, refetchOnWindowFocus: false },
  },
});

// در هر Remote:
import { useQuery } from "@superapp/query";
const { data } = useQuery({
  queryKey: ["food", "orders"],
  queryFn: fetchOrders,
});
```

### ۸-۲) قواعد کلید کش و invalidation

- **namespace اجباری کلید:** هر Remote کلیدهای کش خود را با نام Remote شروع می‌کند (`["food", ...]`) تا تصادم رخ ندهد.
- **invalidation مشترک:** رویدادهای auth (بخش ۲-۳) به `@superapp/query` وصل می‌شوند — در `auth:logout` کل کش پاک می‌شود (`queryClient.clear()`).
- **هماهنگی با `@superapp/api`:** `queryFn` ها از HTTP client مشترک (`@superapp/api`) استفاده می‌کنند تا interceptor توکن و error handling یکسان بماند.

> 📌 این پکیج در بخش ۴ (پکیج‌های مشترک) و در `@superapp/template` (Remote جدید به‌صورت پیش‌فرض query-ready) لحاظ شده است.

---

## ۹) جمع‌بندی موقت / Interim Summary

> ریسک‌ها، سوالات باز و جمع‌بندی نهایی در بخش‌های **۱۳، ۱۴ و ۱۵** (پس از مباحث DX و موبایل) آمده‌اند.

این معماری بر اساس **پاسخ‌های پرسشنامه‌ی مدیر تیم** و **مواردی که در جلسه مطرح شد و تجربه فردی** طراحی شده و با تمام محدودیت‌ها سازگار است:

✅ سوپراپ با تجربه‌ی یکپارچه (Shell + Remote در یک صفحه)  
✅ احراز هویت یک‌بار (OIDC + JWT مشترک)  
✅ دسترسی تفکیک‌شده‌ی تیم‌ها به سورس (مرزبندیِ Nx + `CODEOWNERS`؛ Polyrepo + ACL فقط در صورت نیاز)  
✅ استقلال بیلد/دیپلوی/rollback هر Remote (Module Federation + Pipeline مستقل)  
✅ Design System و منطق مشترک (workspace محلی → Azure Artifacts در فاز ۳)  
✅ پشتیبانی از میکروسرویس‌ها (mock API در PoC → Gateway در فاز ۳)  
✅ مقیاس‌پذیری با template برای Remote جدید  
✅ مسیر واضح: **PoC محلی (فاز ۰–۲)** → **استقرار روی زیرساخت شرکت (فاز ۳)**

**بزرگ‌ترین عامل موفقیت:** اثبات معماری در **PoC محلی** (Federation + auth + UX) قبل از سرمایه‌گذاری روی Azure. استقرار روی زیرساخت شرکت فقط بعد از PoC پایدار.

---

---

## ۱۰) تجربه‌ی توسعه‌دهنده (DX) برای Remote ها / Developer Experience

> 🎯 **چالش:** Remote باید داخل Shell اجرا شود تا workflow واقعی تست شود — ولی در **PoC محلی** نیازی به سرور شرکت یا Azure نیست.

### ۱۰-۰) PoC محلی (فاز ۰–۲): Shell + Remote روی localhost ⭐

در فاز اول، **هر دو** Shell و Remote روی سیستم شخصی اجرا می‌شوند:

```
┌─────────────────────────────────────────────────────────────┐
│  1. cd superapp-poc && bun run dev                          │
│     ├─ Shell  → http://localhost:3000                       │
│     └─ Remote → http://localhost:3001/remoteEntry.js        │
│                                                             │
│  2. مرورگر: http://localhost:3000                           │
│     └─ mock login → Remote بارگذاری می‌شود ✅               │
│                                                             │
│  3. تغییر کد Remote → Hot Reload → فوری در Shell           │
└─────────────────────────────────────────────────────────────┘
```

| ✅ مزیت PoC محلی                   | توضیح                           |
| ---------------------------------- | ------------------------------- |
| بدون Azure / VPN / Identity Server | شروع فوری بعد از تأیید سند      |
| mock auth و mock API               | وابستگی به تیم بک‌اند شرکت نیست |
| Hot reload کامل                    | تجربه‌ی توسعه سریع              |

### ۱۰-۱) شرکت (فاز ۳+): Dev Shell مشترک + Local Remote Override

> پس از استقرار روی زیرساخت شرکت، یک **Dev Shell** روی سرور مشترک (مثلاً `https://dev-shell.company.com`) دیپلوی می‌شود. توسعه‌دهنده‌ی Remote با override، Remote خود را از `localhost` لود می‌کند.

**گردش کار توسعه‌دهنده‌ی remote-food:**

```
┌──────────────────────────────────────────────────────────────┐
│  1. git clone remote-food (ریپوی خودش)                      │
│  2. bun install && bun run dev                               │
│     └─▶ dev server روی http://localhost:3001                │
│                                                              │
│  3. مرورگر: باز کردن Dev Shell مشترک                       │
│     https://dev-shell.company.com                            │
│     └─▶ لاگین واقعی با Identity Server کار می‌کند ✅        │
│                                                              │
│  4. اضافه کردن override:                                    │
│     ?__remote=food@http://localhost:3001/remoteEntry.js      │
│                                                              │
│  5. حالا Shell از سرور، ولی Remote:Food از localhost اجرا   │
│     می‌شود. تغییر کد → Hot Reload → فوری در صفحه            │
└──────────────────────────────────────────────────────────────┘
```

**مکانیزم‌های override:**

| تکنیک                 | چطور کار می‌کند                                       | مناسب برای                           |
| --------------------- | ----------------------------------------------------- | ------------------------------------ |
| **Query param**       | `?__remote=food@http://localhost:3001/remoteEntry.js` | 🟢 ساده‌ترین؛ پیکربندی صفر           |
| **localStorage flag** | `localStorage.setItem('override_food', '...')`        | 🟢 پایدار بین refresh                |
| **نوار ابزار Dev**    | پنل کوچک در Dev Shell برای انتخاب Remote محلی         | 🟢 بهترین UX (نیازمند توسعه‌ی اولیه) |

**ارزیابی:**

| ✅ نقطه قوت                | ❌ نقطه ضعف                                        |
| -------------------------- | -------------------------------------------------- |
| بدون clone کردن Shell      | باید یک Dev Shell مشترک نگه‌داری/دیپلوی شود        |
| لاگین واقعی کار می‌کند     | نیاز به محیط dev بک‌اند (Identity + microservices) |
| Hot reload کامل            |                                                    |
| برای همه‌ی Remote ها یکسان |                                                    |

### ۱۰-۲) راهکار مکمل: Storybook (از فاز ۱)

Storybook کامپوننت‌ها را **ایزوله از Shell** توسعه/تست می‌کند. این **مکمل** است نه جایگزینِ workflow یکپارچه، ولی برای Design System بسیار ارزشمند است و از **فاز ۱** برای `@superapp/ui` راه‌اندازی می‌شود:

- **راه‌اندازی با Nx:** `nx g @nx/react:storybook-configuration shared-ui --bundler=rspack` (targetهای `storybook` و `build-storybook` را می‌سازد؛ اجرا با `nx storybook shared-ui`).
- **RTL/LTR:** یک `globalType` جهت + decorator که `document.documentElement.dir` را از toolbar سوییچ می‌کند — همان `dir` که در اپ روی `<html>` است.
- **توکن‌ها:** فایل CSSِ Storybook همان `@import "tailwindcss"` + `theme.css` را می‌آورد تا کامپوننت‌ها با توکن‌های واقعیِ `@theme` رندر شوند.
- **Visual regression (فاز ۴):** **Chromatic** روی همین storyها در RTL و LTR snapshot می‌گیرد؛ جزئیات در `SuperApp-Implementation-Progress.md` بخش ۵-۷ و ۸-۵.
- در فاز ۴ می‌توان Storybook را به Remoteها هم گسترش داد.

### ۱۰-۳) توصیه

| فاز            | راهکار                                          | نقش                                 |
| -------------- | ----------------------------------------------- | ----------------------------------- |
| **۰–۲ (محلی)** | `nx serve shell` — Shell + Remote روی localhost | 🟢 **راهکار اصلی PoC**              |
| **۱+ (محلی)**  | Storybook برای `@superapp/ui` (RTL/LTR)         | 🟢 مکمل — توسعه‌ی ایزوله‌ی کامپوننت |
| **۳+ (شرکت)**  | Dev Shell مشترک + Override                      | 🟢 **راهکار اصلی تیم** — auth واقعی |

> ⚠️ **امنیت:** Dev Shell نباید به prod وصل شود. به محیط **dev** Identity Server وصل می‌شود (با کاربر/توکن تست). این جداسازی الزامی است.

### ۱۰-۴) مدیریت پکیج: `bun` (اولویت) و `pnpm` (جایگزین)

| موضوع                       | تصمیم                                                                                                                                                             |
| --------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **ابزار اصلی**              | **`bun`** — نصب بسیار سریع، `bun install`/`bun run`/`bun publish`، پشتیبانی از workspace، و dedup بومی وابستگی‌ها                                                 |
| **جایگزین در صورت محدودیت** | **`pnpm`** — اگر ابزاری، پلاگین باندلر، یا **Azure Artifacts** با `bun` مشکل سازگاری داشت، `pnpm` (با `pnpm-workspace.yaml` و lockfile قطعی) جایگزین بی‌دردسر است |
| **چرا این دو؟**             | هر دو **dedup قوی** دارند و مشکل «دو نسخه‌ی React» را (که با `npm link` رخ می‌داد — M4) از بین می‌برند. هر دو با رجیستری npm/Verdaccio/Azure Artifacts سازگارند   |
| **قاعده‌ی یکدستی**          | یک ابزار در کل سازمان انتخاب و **lockfile آن commit** شود؛ ترکیب `bun` و `npm` در یک ریپو ممنوع (تداخل lockfile)                                                  |

**نکات سازگاری (چرا ممکن است به `pnpm` برگردیم):**

- اگر پلاگین/لودر خاص باندلر فقط با اجرای Node/npm تست شده باشد و با runtime `bun` سازگار نباشد.
- اگر احراز هویت feed در **Azure Artifacts** با `bun` به‌سادگی برقرار نشد (`pnpm` پشتیبانی جاافتاده‌تری از `.npmrc` سازمانی دارد).
- اگر ابزارهای CI موجود شرکت روی `pnpm`/`npm` استاندارد شده باشند.

> 📌 **جمع‌بندی:** هدف، **سرعت DX با `bun`** است؛ ولی چون این یک پروژه‌ی سازمانی با زیرساخت Azure است، `pnpm` به‌عنوان **مسیر امنِ عقب‌نشینی** از روز اول در نظر گرفته می‌شود و اسکریپت‌ها طوری نوشته می‌شوند که با هر دو کار کنند (استفاده از `bun run <script>` / `pnpm <script>` روی همان `scripts` در `package.json`).

---

## ۱۱) پشتیبانی موبایل و دسکتاپ / Mobile & Desktop Support

> 🎯 **چالش:** هم موبایل و هم دسکتاپ لازم است، ولی بدون نگه‌داری دو کدبیس مجزا، با این حال دیزاین متفاوت. نسخه‌ی موبایل در آینده به **TWA** تبدیل و در Google Play منتشر می‌شود.

### ۱۱-۱) تصمیمات کلیدی

| تصمیم / Decision | انتخاب / Choice                                          | دلیل                                               |
| ---------------- | -------------------------------------------------------- | -------------------------------------------------- |
| تعداد Shell      | **۱** (واحد، adaptive layout)                            | جلوگیری از دو Remote registry، دو routing، دو auth |
| تعداد Remote     | **۱ به ازای هر ماژول** (مشترک بین موبایل/دسکتاپ)         | کدبیس واحد                                         |
| استراتژی layout  | **Adaptive** در سطح کامپوننت + **Responsive** در سطح CSS | دیزاین واقعاً متفاوت                               |
| PWA              | بله، در Shell                                            | پیش‌نیاز TWA                                       |
| TWA              | بله، در فاز بعد از MVP                                   | فقط اندروید                                        |

### ۱۱-۲) یک Shell، دو Layout (نه دو Shell)

چرا دو Shell نسازیم؟ چون Remote ها در هر دو حالت یکسکند و باید mount point یکسان داشته باشند. دو Shell یعنی دو Remote registry، دو routing logic، دو auth flow — دقیقاً همان «دو نسخه‌ی مجزا»ای که نمی‌خواهیم.

به‌جاش، Shell بر اساس viewport **layout خود را عوض می‌کند**:

| المان / Element | دسکتاپ / Desktop        | موبایل / Mobile                     |
| --------------- | ----------------------- | ----------------------------------- |
| Navigation      | نوار کناری (sidebar)    | Bottom navigation یا hamburger menu |
| Header          | فول‌سایز با breadcrumbs | فشرده، با back button               |
| Layout grid     | چندستونی                | تک‌ستونی                            |
| Modal / Drawer  | modal مرکزی             | bottom sheet                        |

Shell تشخیص می‌دهد موبایل/دسکتاپ و **layout wrapper** مناسب را رندر می‌کند. Remote ها در همان mount point قرار می‌گیرند.

### ۱۱-۳) Adaptive Remote ها (نه صرفاً Responsive)

چون دیزاین موبایل و دسکتاپ متفاوت است، responsive خالص کافی نیست. تفاوت:

| رویکرد         | چطور کار می‌کند                      | مناسب وقتی که                           |
| -------------- | ------------------------------------ | --------------------------------------- |
| **Responsive** | CSS media query، چیدمان جابجا می‌شود | تفاوت ظاهری کوچک                        |
| **Adaptive**   | کامپوننت/چیدمان متفاوت در breakpoint | تفاوت ساختاری (دیزاین واقعاً متفاوت) ✅ |

**الگوی Component Variants در Design System:**

```tsx
// @superapp/ui یک primitive عمومیِ adaptive می‌دهد (نه کامپوننت دامنه‌ای):
function DataList({ items, renderItem }) {
  const { isMobile } = useViewport();
  return isMobile
    ? <CardList  items={items} renderItem={renderItem} />   {/* چیدمان کارت‌های عمودی */}
    : <DataTable items={items} renderItem={renderItem} />;  {/* چیدمان جدولی */}
}

// در خودِ Remote:Food (کامپوننت دامنه‌ای، نه در Design System):
<DataList items={orders} renderItem={(o) => <OrderRow order={o} />} />
```

> 📌 **مرز مالکیت (رفع D-B):** primitiveهای عمومیِ adaptive (`DataList`، `DataTable`، `CardList`) در `@superapp/ui` (تیم Design System) می‌مانند؛ کامپوننت‌های **دامنه‌ای** (مثل `OrderRow`/لیستِ سفارش food) در خودِ Remote تعریف می‌شوند و از این primitiveها استفاده می‌کنند. Design System نباید منطق دامنه‌ای بشناسد.

**قاعده‌ی طراحی:**

- تفاوت **Layout-level** → variant کامپوننت
- تفاوت **ظاهری** → CSS breakpoint

| ✅ مزیت              | توضیح                                  |
| -------------------- | -------------------------------------- |
| کدبیس واحد           | Remote یک‌بار نوشته می‌شود             |
| دیزاین واقعاً متفاوت | هر variant کاملاً مجزا طراحی می‌شود    |
| کاهش بار شناختی      | توسعه‌دهنده فقط `<OrderList>` می‌نویسد |

### ۱۱-۴) PWA-ready Shell (پیش‌نیاز TWA)

TWA یک wrapper اندروید دور Chrome است که وب‌اپ را بدون نوار آدرس نشان می‌دهد. برای اینکه TWA کار کند، وب‌اپ باید **PWA معتبر** باشد.

| نیازمندی PWA / Requirement | توضیح                                                        | تأثیر روی معماری                                      |
| -------------------------- | ------------------------------------------------------------ | ----------------------------------------------------- |
| **`manifest.json`**        | متادیتای اپ (نام، آیکون، رنگ)                                | در Shell اضافه می‌شود                                 |
| **Service Worker**         | **push (FCM)** + precache برای سرعت — **بدون کارکرد آفلاین** | در Shell با Workbox — جزئیات کامل در **بخش ۱۷**       |
| **HTTPS**                  | الزامی در production                                         | PoC محلی: `http://localhost` کافی است؛ شرکت: Azure ✅ |
| **`assetlinks.json`**      | تأیید رابطه‌ی app ↔ وب‌سایت                                  | در دامنه قرار می‌گیرد                                 |
| **App-like UX**            | بدون full reload، fast                                       | Module Federation تضمین می‌کند ✅                     |

> 💡 **نکته‌ی کلیدی:** TWA همان URL اصلی (`app.company.com`) را باز می‌کند. یعنی **هیچ کار اضافه‌ای در Remote ها لازم نیست** — فقط Shell باید PWA باشد. این یک امتیاز بزرگ معماری فعلی است.

> 📌 **جزئیات Service Worker + Push:** با توجه به نیاز قطعی به **push (FCM)** و اهمیت آینده‌ی آن، این زیرسیستم در **بخش ۱۷** تشریح شده است (جریان push، precache برای سرعت، و اشتراک Remote ها). **کارکرد آفلاین در دامنه نیست** و SW صرفاً برای push و کارایی است.

### ۱۱-۵) Deep Linking و TWA

TWA از **URL intent** پشتیبانی می‌کند — کاربر روی لینک `/food/orders/123` کلیک کند، TWA باز می‌شود و مستقیم به همان صفحه می‌رود.

- ✅ مسیریابی Shell که قبلاً طراحی شد (`/food/*`، `/account/*`) **کاملاً با TWA سازگار است**.
- ✅ URL های یکتا و shareable — چه در دسکتاپ، چه در TWA.
- ⚠️ باید مطمئن شویم هیچ حالت فقط-in-memory نیست که با URL نقض شود (state در URL باشد).

### ۱۱-۶) هشدار: iOS و محدودیت‌های TWA

| موضوع / Topic | Android            | iOS                                       |
| ------------- | ------------------ | ----------------------------------------- |
| TWA           | ✅ پشتیبانی می‌شود | ❌ وجود ندارد                             |
| جایگزین iOS   | —                  | PWA (Add to Home Screen) یا **Capacitor** |

> 📌 TWA فقط Android را پوشش می‌دهد. اگر در آینده نسخه‌ی iOS App Store لازم شود، **Capacitor** گزینه است (همان کد وب را در یک WebView بومی‌شده قرار می‌دهد). فعلاً خارج scope، ولی معماری اجازه‌اش را می‌دهد.

### ۱۱-۷) تأثیر بر Design System و Template

| پکیج / Package       | اضافه می‌شود / Added                                                                                           |
| -------------------- | -------------------------------------------------------------------------------------------------------------- |
| `@superapp/ui`       | `useViewport()` hook، breakpoint tokens، الگوی variant برای کامپوننت‌ها، **RTL/logical properties** (بخش ۱۱-۸) |
| `@superapp/template` | Remote جدید به‌صورت پیش‌فرض adaptive و RTL-aware ساخته می‌شود (نمونه‌ی variant داخلش)                          |
| Shell                | `manifest.json`، service worker، `assetlinks.json` hosting، `dir="rtl"` روی ریشه                               |

### ۱۱-۸) RTL از روز اول / RTL-first Design System (رفع M3 / G1)

> 🎯 اپ **داخل‌سازمانی و فارسی** است، پس **RTL یک نگرانی layout است، نه ترجمه.** حتی با اینکه i18n متن به فاز بعد موکول شده (A6)، Design System باید **از فاز ۱ به‌صورت RTL-first** ساخته شود؛ در غیر این صورت بازسازی بعدیِ همه‌ی کامپوننت‌ها بسیار پرهزینه است (هزینه‌ی نزدیک صفر در ابتدا، بسیار بالا در انتها).

| قاعده / Rule                         | توضیح                                                                                                                            |
| ------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------- |
| **Logical properties به‌جای فیزیکی** | `margin-inline`, `padding-inline`, `inset-inline-start/end` به‌جای `left/right`؛ کامپوننت‌ها بدون تغییر در LTR و RTL کار می‌کنند |
| **`dir="rtl"` روی ریشه‌ی Shell**     | همه‌ی Remote ها جهت را از Shell به ارث می‌برند؛ Remote نباید `dir` را override کند                                               |
| **آیکون‌ها و جهت‌دارها**             | آیکون‌های جهت‌دار (فلش back/forward، chevron) به‌صورت خودکار با `[dir="rtl"]` معکوس می‌شوند                                      |
| **بدون مقادیر hard-coded جهت‌دار**   | Stylelint استفاده از `left/right` در Remote ها را رد می‌کند (هم‌راستا با بخش ۷-۲)                                                |
| **تست بصری در هر دو جهت**            | Storybook کامپوننت‌ها را در RTL و LTR رندر می‌کند (فاز ۱)؛ Chromatic برای visual regression (فاز ۴)                              |

> 📌 چون `@superapp/ui` از فاز ۱ RTL-first است، هم چیدمان فارسی امروز درست کار می‌کند و هم اگر روزی i18n/LTR لازم شد (A6)، layout آماده است. این تصمیم با استراتژی CSS Isolation (بخش ۷) و adaptive variants (بخش ۱۱-۳) کاملاً سازگار است.

---

## ۱۲) دیاگرام: معماری موبایل/دسکتاپ / Mobile-Desktop Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                     SHELL (واحد، Adaptive)                      │
│                                                                 │
│   viewport detection → Desktop Layout  OR  Mobile Layout        │
│                                                                 │
│   ┌─────────────────────────┐    ┌───────────────────────────┐  │
│   │      Desktop Layout     │    │      Mobile Layout        │  │
│   │                         │    │                           │  │
│   │  ┌────┐  ┌───────────┐  │    │  ┌───────────────────┐    │  │
│   │  │side│  │   mount   │  │    │  │     mount point   │    │  │
│   │  │bar │  │   point   │  │    │  │   (همان Remote)   │    │  │
│   │  │    │  │  ◀── Remote │    │  │   (همان mount)    │    │  │
│   │  └────┘  └───────────┘  │    │  └───────────────────┘    │  │
│   │                         │    │  ┌───────────────────┐    │  │
│   │  (multi-column grid)    │    │  │ bottom navigation │    │  │
│   │                         │    │  └───────────────────┘    │  │
│   └─────────────────────────┘    └───────────────────────────┘  │
│                                                                 │
│   ┌──────────────────────────────────────────────────────────┐  │
│   │   Auth │ Routing │ Global State │ Design System (shared) │  │
│   └──────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
                                 │
                                 ▼
              ┌──────────────────────────────────────┐
              │         Remote Apps (مشترک)          │
              │                                      │
              │   <DataList>  (primitive UI)         │
              │      ├── isMobile? <CardList>        │  ← Adaptive
              │      └── else     <DataTable>        │     primitive
              └──────────────────────────────────────┘
                                 │
                                 ▼
┌────────────────────────────────────────────────────────────────┐
│                      Distribution (توزیع)                      │
│                                                                │
│   ┌──────────────────┐    ┌────────────────────────────────┐   │
│   │  Desktop Browser │    │  Android TWA                   │   │
│   │  (web app)       │    │  (wrapper دور همان web app)   │   │
│   └──────────────────┘    └────────────────────────────────┘   │
│                                                                │
│   ┌──────────────────────────────────────────────────────────┐ │
│   │  PWA Layer (در Shell):                                   │ │
│   │  • manifest.json   • Service Worker (Workbox)            │ │
│   │  • assetlinks.json (برای TWA)                            │ │
│   └──────────────────────────────────────────────────────────┘ │
└────────────────────────────────────────────────────────────────┘
```

---

## ۱۳) ریسک‌ها و راهکارها / Risks & Mitigations

| #    | ریسک / Risk                                                               | شدت                         | راهکار / Mitigation                                                                                                                                                                                                      |
| ---- | ------------------------------------------------------------------------- | --------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| R1   | **نبود دیپلوی خودکار در شرکت** (سوال ۲۵، ۲۶: خیر)                         | 🟠 بالا (فاز ۳)             | PoC محلی با `bun run build` + preview کافی است. **Azure Pipeline در فاز ۳** راه‌اندازی می‌شود.                                                                                                                           |
| R2   | **زمان‌بندی MVP + راه‌اندازی زیرساخت**                                    | 🟢 پایین/متوسط              | «~۲ ماه» یک **هدف منعطف** است، نه ضرب‌الاجل سخت (طولانی‌تر شدن مانع نیست). با این حال برای کاهش ریسک: MVP محدود به Shell + ۱ Remote؛ اتوماسیون سنگین (`foundation` PR-tool، `template` کامل) در صورت فشار به فاز ۳ (M5). |
| R3   | ✅ **حل‌شده** — توکن واحد با audience مشترک تأیید شد (Q1)                 | —                           | ریسک audience برطرف شد. طراحی `@superapp/auth` آماده‌ی مهاجرت آینده به multi-audience.                                                                                                                                   |
| R4   | **state مشترک بین Remote ها** (سوال ۹)                                    | 🟠 بالا                     | قاعده‌ی صریح: Shell مالک state سراسری؛ Remote فقط consumer. سندسازی و بازبینی کد.                                                                                                                                        |
| R5   | **تداخل نسخه‌ی پکیج مشترک هنگام rollback مستقل** (بخش ۱۶)                 | 🟡 متوسط                    | نسخه‌بندی مستقل (اولویت) + **انضباط سازگاری عقب‌رو در طول major** + **کف و سقف نسخه** (`compatibility.json` ≤ نسخه ≤ `max-in-production.json` — رفع C2) + singleton توسط Host + ترتیب «Shell اول».                       |
| R6   | **تک Team Lead / گلوگاه تصمیم** (سوال ۲۴)                                 | 🟡 متوسط                    | قالب (template) + استانداردهای مکتوب + ADR (Architecture Decision Records) برای تصمیمات.                                                                                                                                 |
| R7   | **refresh token / انقضای نشست**                                           | 🟡 متوسط                    | Shell مسئول refresh خودکار؛ Remote ها از طریق event مطلع می‌شوند.                                                                                                                                                        |
| R8   | **ناسازگاری major نسخه‌ی React بین Remote ها**                            | 🟢 پایین                    | تنها قید سختِ باقی‌مانده: `react` با `singleton + strictVersion` روی یک major قفل می‌شود (بخش ۱۶-۱).                                                                                                                     |
| R9   | **پایش (monitoring) فعلاً مد نظر نیست** (Q5)                              | 🟢 پایین                    | PoC: `console.error` کافی. **Application Insights از فاز ۳/۴** (بخش ۲۴).                                                                                                                                                 |
| R10  | **تبعات امنیتی Module Federation**                                        | 🟡 متوسط                    | Remote ها فقط از منابع تأییدشده (manifest + trusted domains) بارگذاری شوند. **CSP + SRI** (بخش ۲۰).                                                                                                                      |
| R11  | **نگه‌داری Dev Shell مشترک** (بخش ۱۰)                                     | 🟡 متوسط (فاز ۳)            | در PoC محلی لازم نیست. در شرکت: Pipeline Build → Deploy برای Dev Shell.                                                                                                                                                  |
| R12  | **افزایش پیچیدگی کامپوننت‌های Adaptive** (بخش ۱۱)                         | 🟡 متوسط                    | قاعده‌ی صریح «variant فقط برای layout-level»؛ CSS breakpoint برای بقیه؛ تست E2E در دو viewport.                                                                                                                          |
| R13  | **تنظیمات `assetlinks.json` نادرست** → رد TWA توسط Play Store             | 🟡 متوسط                    | بررسی در فاز TWA؛ استفاده از ابزار رسمی Google برای اعتبارسنجی.                                                                                                                                                          |
| R14  | **state در URL نباشد** → deep link در TWA خراب شود (بخش ۱۱)               | 🟡 متوسط                    | قاعده‌ی طراحی: state کلیدی باید در URL (query/route param) نگه‌داری شود، نه فقط در حافظه.                                                                                                                                |
| R15  | **محیط dev بک‌اند هنوز در دسترس نیست** (Q7)                               | 🟢 پایین (PoC)              | PoC با **mock auth + mock API** (MSW/json-server). اتصال به بک‌اند واقعی در **فاز ۳**.                                                                                                                                   |
| R16  | **پراکندگی نسخه‌ها بدون هم‌راستاسازی** — تیم‌ها روی نسخه‌های مختلف بمانند | 🟡 متوسط                    | `foundation` نسخه‌ی canonical را پیشنهاد و **PR/نوتیفیکیشن خودکار** می‌فرستد؛ هم‌راستایی توصیه‌شده ولی اجباری نیست (اولویت با استقلال). CI فقط کف سازگاری را الزام می‌کند.                                               |
| R17  | **پیچیدگی Service Worker + Push** (بخش ۱۷)                                | 🟢 پایین                    | با حذف آفلاین/Background Sync، SW فقط برای push (FCM) و precache است — بسیار ساده‌تر. PoC: بدون SW؛ **فاز ۳** (SW + آماده‌سازی push)؛ **فاز ۴** (فعال‌سازی push).                                                        |
| R17b | **تداخل کش SW با rollback مستقل** (بخش ۱۷)                                | 🟡 متوسط                    | `remoteEntry.js` به‌صورت **network-first** + chunkهای hash-based + versioned manifest → rollback فوری و قطعی (رفع C3، بخش ۱۷-۲-الف).                                                                                     |
| R18  | **توکن واحد، audience مشترک** → ریسک امنیتی (Q1)                          | 🟡 متوسط                    | توکن کوتاه‌مدت (۱۵ دقیقه) + refresh امن؛ آماده‌سازی برای تفکیک آینده.                                                                                                                                                    |
| R19  | **خرابی Remote در production** — کاربر صفحه‌ی خالی ببیند                  | 🟡 متوسط                    | **RemoteLoader wrapper** + Error Boundary + Fallback UI + retry با backoff (بخش ۱۸).                                                                                                                                     |
| R20  | **Bundle size کنترل‌نشده** — بارگذاری کند                                 | 🟡 متوسط                    | **Bundle Budget** در CI از **فاز ۴** (Shell < 200KB، هر Remote < 150KB gzip) (بخش ۲۰).                                                                                                                                   |
| R21  | **تست ناکافی مرز Remote ↔ Shell**                                         | 🟡 متوسط                    | PoC: تست دستی کافی. Integration/Contract Test در **فاز ۴** به Pipeline اضافه می‌شوند (بخش ۱۹).                                                                                                                           |
| R22  | **Breaking change در پکیج مشترک بدون اطلاع‌رسانی**                        | 🟡 متوسط                    | Governance (بخش ۲۲): semver سخت‌گیرانه + deprecation notice + codemod + فترة migration.                                                                                                                                  |
| R23  | **نشت استایل CSS بین Remote ها** (بخش ۷)                                  | 🟡 متوسط                    | **تمِ مشترک (`@theme`) + preflight فقط در Shell** + منع CSS-in-JS + Stylelint در CI (رفع M1).                                                                                                                            |
| R24  | **منطق fetch/cache پراکنده بین Remote ها**                                | 🟡 متوسط                    | لایه‌ی **server-state مشترک با `@superapp/query`** (TanStack Query singleton) + namespace کلید کش + invalidation مشترک (رفع M2، بخش ۸).                                                                                  |
| R25  | **بازسازی پرهزینه‌ی RTL در آینده**                                        | 🟠 بالا (اگر دیر انجام شود) | Design System **RTL-first از فاز ۱** با logical properties (رفع M3/G1، بخش ۱۱-۸) — هزینه‌ی نزدیک صفر در ابتدا.                                                                                                           |
| R26  | **دام «دو نسخه‌ی React» با `npm link` در dev**                            | 🟢 پایین                    | مدیریت پکیج با **`bun`/`pnpm`** + **Monorepo workspace** به‌جای `link` + dedup بومی (رفع M4، بخش ۲-۲ و ۱۰-۴).                                                                                                            |
| R27  | **CSRF روی endpoint refresh (cookie-based)**                              | 🟡 متوسط                    | `SameSite=Strict`/`Lax` + anti-CSRF token در `@superapp/auth` (رفع m4، بخش ۲-۳).                                                                                                                                         |
| R28  | **manifest مرکزی = نقطه‌ی شکست واحد**                                     | 🟡 متوسط                    | کش نسخه‌دارِ آخرین manifest سالم + سرو دو-مبدأ + degradation کنترل‌شده (رفع m6، بخش ۳).                                                                                                                                  |

---

## ۱۴) سوالات تصمیم‌گیری — پاسخ‌ها و تأثیرها / Decisions — Answers & Impacts

پاسخ‌های نهایی مدیر تیم به سوالات باز، و تأثیر آن‌ها بر معماری:

| #   | سوال / Question           | پاسخ / Answer                                      | تأثیر بر معماری / Impact                                                                                                                                                             |
| --- | ------------------------- | -------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Q1  | توکن چند-audience؟        | **توکن واحد، audience مشترک** بین همه‌ی سرویس‌ها   | ✅ ساده‌ترین حالت؛ فقط یک توکن در Shell نگه‌داری می‌شود. ⚠️ ریسک امنیتی: لو رفتن توکن = دسترسی به همه‌ی سرویس‌ها. طراحی `@superapp/auth` باید آماده‌ی مهاجرت به multi-audience باشد. |
| Q2  | API Gateway؟              | **بله — میکروسرویس + API Gateway میانی**           | ✅ CORS و توکن‌سازی متمرکز در Gateway؛ Remote ها فقط به Gateway وصل می‌شوند، نه مستقیماً به سرویس‌ها.                                                                                |
| Q3  | مرورگرهای هدف؟            | **اپ داخل‌سازمانی — نسخه‌ی مرورگر مهم نیست**       | ✅ فقط evergreen browsers (Chrome/Edge/Firefox جدید). ریسک مرورگر حذف می‌شود؛ می‌توان از جدیدترین API ها استفاده کرد.                                                                |
| Q4  | استراتژی host Remote ها؟  | **مسیر مشترک زیر یک دامنه** (پاسخ سوال ۵ پرسشنامه) | ✅ Remote ها در `app.company.com/remotes/{name}/` سرو می‌شوند؛ مشکل CORS به حداقل.                                                                                                   |
| Q5  | پایش (monitoring) در MVP؟ | **فعلاً خیر — فاز بعد**                            | ✅ Application Insights به فاز ۳ موکول می‌شود. حداقل لاگ خطا توصیه می‌شود.                                                                                                           |
| Q6  | مالکیت Design System؟     | **مالکیت در اختیار یک تیم تخصصی**، بقیه مصرف‌کننده | ✅ ساختار تیمی روشن؛ آن تیم مسئول انتشار `@superapp/ui` در Azure Artifacts.                                                                                                          |
| Q7  | محیط dev بک‌اند؟          | **هنوز در دسترس نیست، در دستور کار است**           | ✅ PoC با mock auth/API (R15). اتصال به بک‌اند واقعی در **فاز ۳** (زیرساخت شرکت).                                                                                                    |
| Q8  | iOS در آینده؟             | **فعلاً خیر**                                      | ✅ TWA فقط اندروید کفایت می‌کند. Capacitor برای آینده‌ی دورتر بررسی نشد.                                                                                                             |
| Q9  | breakpoint موبایل/دسکتاپ؟ | **پیش‌فرض**: ≤768px = موبایل                       | ✅ استاندارد صنعت؛ در `@superapp/ui` قید می‌شود. قابل تنظیم بعداً.                                                                                                                   |
| Q10 | Service Worker + Push؟    | **بله برای Push (FCM) — آفلاین ارزشی ندارد**       | ✅ SW فقط برای **push (VAPID/FCM)** و precache برای سرعت. **Background Sync و کارکرد آفلاین حذف شد** → زیرسیستم ساده‌تر و بدون تناقض با توکن memory (بخش ۱۷).                        |

### ۱۴-۱) دو تصمیم معماری بزرگ حاصل از پاسخ‌ها

دو پاسخ، طراحی کلی را تغییر می‌دهند و در بخش‌های اختصاصی توضیح داده شده‌اند:

1. **نسخه‌بندی مستقل + هم‌راستاسازی توصیه‌شده** — rollback/نسخه‌ی مستقل هر Remote اولویت است؛ هم‌نسخه بودن یک هدف توصیه‌شده (نه اجبار) با انضباط سازگاری عقب‌رو. → بخش ۱۶
2. **Service Worker برای Push (FCM)** — بدون کارکرد آفلاین؛ SW فقط برای push و کارایی. → بخش ۱۷

---

## ۱۵) جمع‌بندی نهایی / Final Conclusion

این معماری بر اساس **پاسخ‌های پرسشنامه‌ی مدیر تیم** و **۱۰ سوال تصمیم‌گیری تکمیلی** طراحی شده و با تمام محدودیت‌ها سازگار است:

✅ سوپراپ با تجربه‌ی یکپارچه (Shell + Remote در یک صفحه)
✅ احراز هویت یک‌بار (OIDC + توکن واحد JWT مشترک — Q1)
✅ ارتباط از طریق **API Gateway میانی** (Q2) — CORS متمرکز
✅ دسترسی تفکیک‌شده‌ی تیم‌ها به سورس (مرزبندیِ Nx + `CODEOWNERS`؛ Polyrepo + ACL فقط در صورت نیاز)
✅ استقلال بیلد/دیپلوی/rollback هر Remote (Module Federation + Pipeline مستقل)
✅ Design System و منطق مشترک (workspace محلی → Azure Artifacts در فاز ۳)
✅ **نسخه‌بندی و rollback مستقل هر Remote** (اولویت) + هم‌راستاسازی توصیه‌شده با `@superapp/foundation` و انضباط سازگاری عقب‌رو (بخش ۱۶)
✅ **Design System مالک یک تیم تخصصی** (Q6)
✅ پشتیبانی از میکروسرویس‌ها (mock API در PoC → Gateway در فاز ۳)
✅ مقیاس‌پذیری با template برای Remote جدید
✅ مسیر واضح: **PoC محلی (فاز ۰–۲)** → **استقرار روی زیرساخت شرکت (فاز ۳)** → **تکمیل (فاز ۴)**
✅ تجربه‌ی توسعه: localhost در PoC؛ Dev Shell + Override در شرکت (بخش ۱۰)
✅ پشتیبانی موبایل و دسکتاپ با کدبیس واحد (Adaptive layout + Component Variants — Q9)
✅ **Service Worker برای Push (FCM)** + precache برای سرعت — **بدون کارکرد آفلاین** — فاز ۳/۴ (Q10، بخش ۱۷)
✅ آماده‌سازی TWA (PWA-ready Shell + Deep Linking) — فاز ۴
✅ **State سراسری با Zustand** — سبک، بدون boilerplate، مناسب federation
✅ **Resilience** — خرابی Remote کل اپ را از بین نمی‌برد (Fallback + Error Boundary + Retry)
✅ **Testing Strategy** — هرم تست + Contract Testing _(Pipeline کامل در فاز ۴)_
✅ **Security Hardening** — CSP، SRI، Trusted Domains _(production — فاز ۳+)_
✅ **Performance** — Bundle Budget، Code Splitting، CDN _(فاز ۴)_
✅ **API Governance** — OpenAPI + Type Generation + `@superapp/config`
✅ **Governance** — Ownership Matrix، ADR، Release Process، Breaking Change management
✅ **Deployment Strategy** — Canary، Blue/Green، Feature Flags _(فاز ۴)_
✅ **Observability** — Application Insights _(فاز ۳/۴)_
✅ **Module Federation 2.0** (`@module-federation/enhanced`) — حذف ۳۰–۴۰٪ کد زیرساختی سفارشی (C1)
✅ **مدل نسخه‌بندی با کف و سقف** — بستن نقص forward-compatibility (C2)
✅ **rollback فوری و قطعی** — `remoteEntry.js` network-first + hash-based (C3)
✅ **استایل‌دهی** — Tailwind v4 + `cva` + `clsx`/`tailwind-merge` + توکن‌های `@theme`؛ ایزوله‌سازی MF با تمِ مشترک + preflight فقط در Shell؛ منع CSS-in-JS (M1، بخش ۷)
✅ **Server-State** — `@superapp/query` (TanStack Query singleton) (M2، بخش ۸)
✅ **RTL-first** — Design System با logical properties از فاز ۱ (M3/G1، بخش ۱۱-۸)
✅ **مدیریت پکیج با `bun`** (جایگزین `pnpm`) — dedup بومی، رفع دام `npm link` (M4، بخش ۱۰-۴)

**بزرگ‌ترین عوامل موفقیت:**

1. **اثبات معماری در PoC محلی** (فاز ۰–۲) قبل از Azure — Federation، mock auth، UX یکپارچه.
2. **استقرار مرحله‌ای روی زیرساخت شرکت** (فاز ۳) — push ریپوی Nx به Azure DevOps، Pipeline با `nx affected`، OIDC واقعی. (split به Polyrepo + Azure Artifacts فقط در صورت نیاز به ACL سخت.)
3. **انضباط سازگاری عقب‌رو در پکیج‌های مشترک** — چیزی که rollback مستقل را ایمن می‌کند (بخش ۱۶).
4. **قاعده‌ی «Shell مالک state»** — بدون آن، Remote ها ناپایدار می‌شوند.
5. **Governance و ADR** — استانداردهای مکتوب برای تصمیمات پراکنده.

---

## ۱۸) Resilience & خطایابی Module Federation / Federation Resilience

> 🎯 در معماری micro-frontend، **خرابی یک Remote نباید کل اپ را از بین ببرد**. این بخش رفتار سیستم را در سناریوهای خطا تعریف می‌کند.

### ۱۸-۱) خطاهای ممکن در Federation

| خطا / Error            | توضیح                                                     | تأثیر                   |
| ---------------------- | --------------------------------------------------------- | ----------------------- |
| **Remote Unreachable** | `remoteEntry.js` قابل بارگذاری نیست (سرور down، شبکه قطع) | Remote بارگذاری نمی‌شود |
| **Module Not Found**   | `remoteEntry.js` بارگذاری شد ولی expose ای پیدا نشد       | خطای زمان اجرا          |
| **Version Mismatch**   | Remote با نسخه‌ی متفاوت React شروع به کار می‌کند          | دو نسخه‌ی React → crash |
| **Slow Loading**       | Remote کند بارگذاری می‌شود (شبکه ضعیف)                    | UX بد، صفحه‌ی خالی      |
| **Runtime Error**      | Remote بارگذاری شد ولی در رندر crash کرد                  | صفحه‌ی سفید             |

### ۱۸-۲) ماتریس راهکار / Mitigation Matrix

| خطا                    | راهکار                                 | پیاده‌سازی                                                          |
| ---------------------- | -------------------------------------- | ------------------------------------------------------------------- |
| **Remote Unreachable** | 🔄 **Retry + Fallback UI**             | ۳ retry با backoff (200ms, 500ms, 1s)؛ سپس Fallback Component       |
| **Module Not Found**   | ⚠️ **Error Boundary**                  | `RemoteErrorBoundary` خطا را capture و Fallback نشان می‌دهد         |
| **Version Mismatch**   | 🛡️ **Version Negotiation + singleton** | `shared: { react: { singleton: true, requiredVersion } }` در config |
| **Slow Loading**       | ⏳ **Suspense + Skeleton**             | `<Suspense fallback={<RemoteSkeleton />}>`                          |
| **Runtime Error**      | 🛡️ **Error Boundary +.Reporting**      | Error Boundary + ارسال به Application Insights                      |

### ۱۸-۳) الگوی Fallback Component

هر mount point Remote با یک **wrapper** احاطه می‌شود که رفتار resilient دارد:

```tsx
// در Shell — هر Remote در این wrapper قرار می‌گیرد
<RemoteLoader
  remote="food"
  fallback={<RemoteUnavailable remoteName="غذا" onRetry={...} />}
  errorBoundary={<RemoteError remoteName="غذا" />}
  loading={<RemoteSkeleton />}
  retry={{ attempts: 3, backoff: [200, 500, 1000] }}
>
  <RemoteFoodApp />
</RemoteLoader>
```

**رفتار:**

- **در حال بارگذاری:** `RemoteSkeleton` (skeleton UI)
- **پس از خطای شبکه (۳ retry):** «بخش غذا فعلاً در دسترس نیست» + دکمه‌ی «تلاش مجدد»
- **پس از runtime error:** «خطایی رخ داد» + گزارش خودکار به Application Insights
- **موفق:** Remote معمولی نمایش داده می‌شود

> 📌 این wrapper در `@superapp/template` پیش‌فرض قرار می‌گیرد تا همه‌ی Remote ها resilient باشند.
>
> 🔧 **پیاده‌سازی روی MF 2.0 (رفع C1):** به‌جای نوشتن منطق retry/error از صفر، `RemoteLoader` روی **runtime plugin** و hookِ `errorLoadRemote` در `@module-federation/enhanced` سوار می‌شود. بارگذاری Remote، مذاکره‌ی نسخه و بازیابی خطا **بومیِ** MF 2.0 است؛ ما فقط UI (Fallback/Skeleton/Retry) و گزارش خطا را روی آن می‌گذاریم. این حدود ۳۰–۴۰٪ کد زیرساختی این بخش را حذف می‌کند.

### ۱۸-۴) Version Negotiation در Federation

Module Federation قابلیت مذاکره‌ی نسخه را دارد. ⚠️ **دقت (رفع M-A):** به‌صورت پیش‌فرض MF «بالاترین نسخه‌ی ثبت‌شده» را فعال می‌کند، نه لزوماً نسخه‌ی Host. برای اینکه **Shell نسخه‌ی singleton فعال را تعیین کند**، ترکیبِ «قاعده‌ی سقف (بخش ۱۶-۲) + ترتیب Shell-اول» لازم است — این invariant حیاتی است: هنگام ارتقا، ابتدا Shell با نسخه‌ی جدید دیپلوی می‌شود، سپس Remoteها اجازه‌ی bump دارند.

```js
// webpack.config.js (federation config)
shared: {
  // تنها قید سخت: major نسخه‌ی React
  react:        { singleton: true, strictVersion: true,  requiredVersion: "^18.0.0" },
  "react-dom":  { singleton: true, strictVersion: true,  requiredVersion: "^18.0.0" },

  // باید singleton باشد وگرنه Router context مشترک نمی‌شود (M-D)
  "react-router-dom": { singleton: true, strictVersion: false, requiredVersion: "^6.0.0" },

  // پکیج‌های مشترک: singleton ولی tolerant → mismatch باعث crash نمی‌شود
  zustand:                 { singleton: true, strictVersion: false },
  "@superapp/auth":        { singleton: true, strictVersion: false, requiredVersion: "^1.0.0" },
  "@superapp/ui":          { singleton: true, strictVersion: false, requiredVersion: "^1.0.0" },
  "@tanstack/react-query": { singleton: true, strictVersion: false, requiredVersion: "^5.0.0" },
}
```

- `singleton: true` → **هرگز** دو نسخه‌ی همزمان بارگذاری نمی‌شوند؛ نسخه‌ی فعال «بالاترین ثبت‌شده» است و **با قاعده‌ی سقف** عملاً همان نسخه‌ی Host می‌شود.
- `strictVersion: true` **فقط برای React** → اگر major ناسازگار بود، بیلد/بارگذاری fail می‌شود (تنها قید سخت — بخش ۱۶-۱).
- `strictVersion: false` برای پکیج‌های مشترک → **rollback مستقل Remote را ممکن می‌کند** (بخش ۱۶). ایمنی این حالت به **انضباط سازگاری عقب‌رو در طول یک major** وابسته است — نه به هم‌نسخه بودن اجباری.

### ۱۸-۵) Error Boundary سراسری در Shell

علاوه بر Error Boundary هر Remote، Shell یک **Global Error Boundary** دارد که در صورت خرابی بحرانی (مثلاً crash کل Shell)، یک صفحه‌ی fallback کامل نشان می‌دهد با گزینه‌ی «بارگذاری مجدد» و گزارش خطا.

---

## ۱۹) استراتژی تست / Testing Strategy

> 🎯 در معماری micro-frontend، **تست باید مرز بین Remote ها را پوشش دهد** — نه فقط داخل Remote ها.

### ۱۹-۱) هرم تست / Testing Pyramid

```
                        ┌───────────┐
                        │    E2E    │  ← کم، پرهزینه، کلی (Shell مالک)
                        └───────────┘
                     ┌──────────────────┐
                     │  Integration /   │  ← Shell + Remote با هم
                     │  Contract        │
                     └──────────────────┘
                  ┌─────────────────────────┐
                  │ Component / Visual Reg. │  ← Design System تیم
                  └─────────────────────────┘
               ┌───────────────────────────────┐
               │        Unit Tests              │  ← زیاد، سریع، هر Remote
               └───────────────────────────────┘
```

### ۱۹-۲) ماتریس مسئولیت تست / Test Responsibility Matrix

| نوع تست / Test Type            | مسئول / Owner                                                                 | ابزار پیشنهادی / Tool       | دامنه / Scope                        |
| ------------------------------ | ----------------------------------------------------------------------------- | --------------------------- | ------------------------------------ |
| **Unit**                       | هر Remote                                                                     | Vitest / Jest               | منطق داخلی، hooks، utils             |
| **Component**                  | هر Remote                                                                     | React Testing Library       | رندر کامپوننت، تعامل                 |
| **Integration**                | Shell (با همکاری Remote)                                                      | React Testing Library + MSW | Shell + Remote با هم در حالت آزمایشی |
| **Contract (consumer-driven)** | **Remote (مصرف‌کننده) قرارداد را می‌نویسد؛ Backend آن را verify می‌کند** (m3) | Pact / OpenAPI validation   | سازگاری Remote ↔ API                 |
| **E2E**                        | Shell                                                                         | Playwright / Cypress        | سناریوهای کامل کاربر                 |
| **Visual Regression**          | تیم Design System                                                             | Chromatic / Storybook       | یکپارچگی بصری کامپوننت‌ها            |
| **Accessibility**              | هر Remote + Shell                                                             | axe-core                    | WCAG compliance                      |

### ۱۹-۳) Contract Testing (مهم)

چون Remote ها و API جدا تطویر می‌شوند، باید **قرارداد** بینشان مشخص باشد:

| قرارداد / Contract           | ابزار                                                                                                       | توضیح                   |
| ---------------------------- | ----------------------------------------------------------------------------------------------------------- | ----------------------- |
| Remote ↔ Shell (state, auth) | TypeScript types در `@superapp/types`                                                                       | تضمین compile-time      |
| Remote ↔ API Gateway         | **OpenAPI** → type generation؛ contract مصرف‌کننده‌محور توسط Remote تعریف و در CI بک‌اند verify می‌شود (m3) | قرارداد رسمی API        |
| Remote ↔ Remote (events)     | Type-safe event bus                                                                                         | تضمین نام/type event ها |

> 📌 جزئیات OpenAPI و type generation در **بخش ۲۱** (API Governance) آمده است.

### ۱۹-۴) تست در CI

**PoC محلی (فاز ۰–۲):** بدون CI. تست **دستی** در IDE + `bun run build` + preview محلی.

**فاز ۳ (شرکت):** Pipeline ساده Build → Deploy.

**فاز ۴ و بعد:** Pipeline تکمیل می‌شود:

1. Lint (ESLint + Stylelint)
2. Type Check (`tsc --noEmit`)
3. Unit Test (Vitest)
4. Component Test (RTL)
5. Build
6. Bundle Size Check (بخش ۲۰)
7. Contract Test (در صورت وجود قرارداد)
8. Deploy
9. Smoke Test (پس از deploy)

---

## ۲۰) امنیت و کارایی / Security & Performance Hardening

### ۲۰-۱) امنیت / Security

| کنترل / Control                                                  | توضیح                                                                                                                                                                                                                              | لایه / Layer              |
| ---------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------- |
| **Content Security Policy (CSP)**                                | هدر CSP که منابع مجاز را محدود می‌کند (script/style/connect). Remote ها باید در `script-src` مجاز باشند.                                                                                                                           | Shell (هدر)               |
| **Subresource Integrity (SRI)** 🎯 هدف (راهکار نامشخص — رفع M-C) | هش `remoteEntry.js` برای تأیید دست‌نخورده بودن. ⚠️ MF اسکریپت remote را **دینامیک** بارگذاری می‌کند و اعمالِ `integrity` مرورگری خودکار/پایدار نیست؛ گزینه‌ی جایگزینِ عملی‌تر: **signature validation در سطح manifest** (ردیف زیر) | Manifest مرکزی            |
| **Trusted Domains**                                              | لیست دامنه‌های مجاز برای Remote در manifest (نه URL دلخواه)                                                                                                                                                                        | Manifest + Shell          |
| **Signature Validation**                                         | (آینده) امضای دیجیتال `remoteEntry.js` با کلید خصوصی تیم پلتفرم                                                                                                                                                                    | CI publish + Shell verify |
| **Token Security**                                               | access token کوتاه‌مدت (۱۵ دقیقه) + refresh در httpOnly cookie (بخش ۲-۳)                                                                                                                                                           | `@superapp/auth`          |
| **XSS Protection**                                               | خروجی‌های کاربر همیشه escape شوند؛ ESLint rule برای `dangerouslySetInnerHTML`                                                                                                                                                      | همه‌جا                    |
| **HTTPS Everywhere**                                             | اجباری (HSTS)                                                                                                                                                                                                                      | زیرساخت                   |

**CSP نمونه (سخت‌گیرانه — رفع m2):**

```
Content-Security-Policy:
  default-src 'self';
  script-src 'self' https://app.company.com/remotes/ 'nonce-{RANDOM}';
  style-src 'self' 'nonce-{RANDOM}';
  connect-src 'self' https://api.company.com https://idp.company.com;
  img-src 'self' data: https:;
  font-src 'self' data:;
```

> 📌 Remote ها باید **همگی زیر دامنه‌ی مجاز** (`/remotes/`) سرو شوند تا CSP آن‌ها را مجاز کند.

> 🔒 **بدون `style-src 'unsafe-inline'` (رفع m2):** چون Tailwind به یک **فایل CSS استاتیک** کامپایل می‌شود و CSS-in-JS ممنوع است (بخش ۷)، نیازی به `'unsafe-inline'` برای style نداریم.
>
> 🔒 **CSP روی هاستینگ استاتیک (رفع M-B):** چون Shell به‌صورت **static build** روی CDN/Static Site سرو می‌شود و HTML را per-request رندر نمی‌کند، تولیدِ **nonce تازه به‌ازای هر پاسخ ممکن نیست**. دو مسیر مجاز:
>
> - **مسیر پیش‌فرض — CSP مبتنی بر hash:** هشِ اسکریپت/استایل‌های inline در build محاسبه و در هدر CSP (به‌صورت فایل استاتیک یا از طریق تنظیمات CDN/Static Web App) قرار می‌گیرد. چون Tailwind به CSS استاتیک کامپایل می‌شود و CSS-in-JS ممنوع است، سطحِ inline بسیار کم است.
> - **مسیر جایگزین — لایه‌ی edge/reverse-proxy:** اگر nonce پویا لازم شد، یک edge function (مثل Azure Front Door / App Service) هدرِ CSP را با nonce per-request تزریق می‌کند. در نمونه‌ی بالا `'nonce-{RANDOM}'` فقط در این حالت معنا دارد.
>
> ⚠️ **نکته‌ی MF:** Module Federation برای تزریق پویای script ممکن است به تنظیم خاص نیاز داشته باشد؛ در حالت nonce/edge، `@module-federation/enhanced` از `__webpack_nonce__` پشتیبانی می‌کند؛ در حالت hash-based باید اسکریپت‌های تولیدیِ MF در whitelistِ هش‌ها لحاظ و در CI تأیید شوند.

### ۲۰-۲) کارایی / Performance

| معیار / Metric                     | هدف / Target | ابزار / Tool  |
| ---------------------------------- | ------------ | ------------- |
| **LCP** (Largest Contentful Paint) | < 2.5s       | Lighthouse CI |
| **TTI** (Time to Interactive)      | < 3.5s       | Lighthouse CI |
| **Bundle Size (Shell)**            | < 200KB gzip | Bundle Budget |
| **Bundle Size (هر Remote)**        | < 150KB gzip | Bundle Budget |
| **remoteEntry.js**                 | < 50KB gzip  | Bundle Budget |

**استراتژی کارایی:**

| تکنیک / Technique            | پیاده‌سازی                                                                                                                                                                        |
| ---------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Bundle Budget**            | `bundlesize` یا `size-limit` در CI از **فاز ۳** — اگر budget رد شد، بیلد fail                                                                                                     |
| **Code Splitting**           | هر route یک chunk؛ lazy loading Remote ها (Suspense)                                                                                                                              |
| **Tree Shaking**             | ESM، sideEffects: false در package.json پکیج‌ها                                                                                                                                   |
| **Shared-deps / dedup (m5)** | depهای سنگینِ مشترک بین ≥۲ Remote (مثل `@tanstack/react-query`، آیکون، `date-fns`/`dayjs`) در `shared` federation قید شوند تا در هر Remote تکرار نشوند. سیاست در بخش ۴ مکتوب است. |
| **Prefetch**                 | Remote های پراستفاده prefetch شوند (وقت idle)                                                                                                                                     |
| **Preload**                  | critical assets (فونت، CSS اصلی)                                                                                                                                                  |
| **Brotli Compression**       | فشرده‌سازی بهتر از gzip — در CDN/host                                                                                                                                             |
| **CDN**                      | همه‌ی static assets از CDN سرو شوند (Azure CDN / Front Door)                                                                                                                      |

---

## ۲۱) API Governance و پیکربندی مشترک / API Governance & Shared Config

### ۲۱-۱) پکیج جدید: `@superapp/config`

یک پکیج مشترک برای **تمام تنظیمات محیطی و feature flags**:

| وظیفه / Responsibility | مثال                                   |
| ---------------------- | -------------------------------------- |
| **Environment**        | `dev`, `staging`, `prod` detection     |
| **API URLs**           | `apiGatewayUrl`, `authServerUrl`       |
| **Remote URLs**        | manifest آدرس Remote ها (به‌ازای محیط) |
| **Feature Flags**      | toggle قابلیت‌ها بدون deploy           |

```ts
// @superapp/config — نام env varها بدونِ پیشوندِ باندلرمحور (سازگار با Webpack/Rspack؛ در صورت Vite پیشوند VITE_ لازم است) — رفع M-E
export const config = {
  env: process.env.NODE_ENV ?? "development",
  api: {
    gateway: envVar("APP_API_GATEWAY_URL", "https://api.company.com"),
    auth: envVar("APP_AUTH_URL", "https://idp.company.com"),
  },
  remotes: {
    food: {
      dev: "...",
      prod: "https://app.company.com/remotes/food/remoteEntry.js",
    },
    account: {
      dev: "...",
      prod: "https://app.company.com/remotes/account/remoteEntry.js",
    },
  },
  features: {
    pushNotifications: false, // فاز ۳ (FCM)
    // offlineMode حذف شد — کارکرد آفلاین در دامنه نیست (بخش ۱۷)
  },
};
```

> 📌 این پکیج جایگزین پراکندگی تنظیمات در Remote ها می‌شود — **single source of truth برای config**.

### ۲۱-۲) API Governance با OpenAPI

| فرآیند / Process    | توضیح                                                                                          |
| ------------------- | ---------------------------------------------------------------------------------------------- |
| **تعریف قرارداد**   | هر microservice قرارداد خود را به‌صورت **OpenAPI (Swagger)** تعریف می‌کند                      |
| **Type Generation** | از OpenAPI، **TypeScript types** به‌صورت خودکار تولید می‌شود (`openapi-typescript` یا `orval`) |
| **نشر پکیج**        | types در `@superapp/api-{service}` (مثلاً `@superapp/api-food`) نسخه‌بندی و منتشر می‌شود       |
| **مصرف در Remote**  | Remote این پکیج را نصب می‌کند → تضمین تطابق type با API واقعی                                  |

```
┌─────────────┐   OpenAPI spec   ┌──────────────────┐   npm package   ┌──────────────┐
│ Backend     │────────────────▶│ Type Generation  │────────────────▶│ Remote       │
│ (microsvc)  │                  │ (CI pipeline)    │  @superapp/     │ (consumer)   │
│             │                  │                  │  api-food@1.2.0 │              │
└─────────────┘                  └──────────────────┘                 └──────────────┘
```

**مزایا:**

- ✅ **تضمین تطابق type** بین فرانت‌اند و بک‌اند (compile-time)
- ✅ **تغییرات breaking** بلافاصله در CI Remote کشف می‌شود
- ✅ **مستندات خودکار** API

### ۲۱-۳) مدیریت تغییر API / API Change Management

| نوع تغییر / Change Type                | تأثیر               | فرآیند                                                                        |
| -------------------------------------- | ------------------- | ----------------------------------------------------------------------------- |
| **Non-breaking** (افزودن فیلد اختیاری) | بدون تأثیر          | پکیج bump patch/minor                                                         |
| **Breaking** (حذف/تغییر فیلد)          | Remote شکست می‌خورد | (۱) نسخه‌ی جدید API منتشر (۲) Remote آپدیت (۳) نسخه‌ی قدیمی deprecate (۴) حذف |

---

## ۲۲) حاکمیت معماری و مالکیت / Architecture Governance & Ownership

### ۲۲-۱) Ownership Matrix

| ریپو/پکیج / Repo or Package                           | مالک / Owner          | دسترسی تیم‌ها / Team Access        |
| ----------------------------------------------------- | --------------------- | ---------------------------------- |
| `shell`                                               | تیم پلتفرم            | فقط خواندن برای بقیه               |
| `@superapp/foundation`                                | تیم پلتفرم            | فقط خواندن                         |
| `@superapp/ui` (Design System)                        | **تیم Design System** | فقط خواندن (consume via Artifacts) |
| `@superapp/auth`, `@superapp/api`, `@superapp/config` | تیم پلتفرم            | فقط خواندن                         |
| `@superapp/template`                                  | تیم پلتفرم            | فقط خواندن                         |
| `@superapp/api-{service}`                             | تیم صاحب سرویس        | نوشتن                              |
| `remote-food`                                         | تیم غذا               | نوشتن ( فقط ریپوی خودشان)          |
| `remote-account`                                      | تیم حساب              | نوشتن                              |

> 📌 **CODEOWNERS** در هر ریپو، مالک را برای PR enforcement می‌کند.

### ۲۲-۲) ADR (Architecture Decision Records)

هر تصمیم مهم معماری در یک **ADR** مکتوب می‌شود:

```
docs/adr/
├── 0001-module-federation-v2-over-v1.md   ← MF 2.0 (@module-federation/enhanced) — C1
├── 0002-nx-monorepo-polyrepo-if-needed.md  ← Nx Monorepo مسیر اصلی؛ Polyrepo فقط در صورت نیاز به ACL سخت
├── 0003-independent-versioning-with-alignment.md  ← شامل کف/سقف نسخه — C2
├── 0004-zustand-for-client-state.md
├── 0005-tanstack-query-for-server-state.md ← M2
├── 0006-react-18-over-19.md                ← m1
├── 0007-bun-package-manager-pnpm-fallback.md
├── 0008-tailwind-cva-styling-strategy.md   ← M1 (Tailwind + cva + tokens؛ preflight فقط در Shell)
├── 0009-rtl-first-design-system.md         ← M3/G1
├── 0010-application-insights.md
├── 0011-rspack-bundler-from-phase-0.md     ← Rspack از فاز ۰
├── 0012-nx-rspack-module-federation.md     ← NxModuleFederationPlugin + module-federation.config
├── 0013-nx-enforce-module-boundaries.md    ← ACL نرم به‌جای Polyrepo زودهنگام
└── ...
```

**ساختار هر ADR:**

- **Context** (زمینه)
- **Decision** (تصمیم)
- **Alternatives Considered** (جایگزین‌ها)
- **Consequences** (پیامدها)

> ADR ها در یک پوشه‌ی `docs/adr/` در ریپوی `@superapp/foundation` نگه‌داری می‌شوند (به‌عنوان مرجع مرکزی).

### ۲۲-۳) فرآیند انتشار نسخه‌ی پکیج‌های مشترک / Release Process

```
┌──────────────────┐    PR    ┌─────────────────┐   review+merge  ┌────────────────┐
│ developer        │────────▶│ feature branch  │────────────────▶│ main branch    │
│ (تیم Design Sys) │          │ + changeset     │                 │                │
└──────────────────┘          └─────────────────┘                 └───────┬────────┘
                                                                          │
                                                                          ▼
                                                               ┌────────────────────┐
                                                               │ CI Pipeline        │
                                                               │ • build + test     │
                                                               │ • changeset → ver  │
                                                               │ • publish Artifacts│
                                                               │ • notify consumers │
                                                               └────────────────────┘
```

- از **Changesets** (یا semantic-release) برای نسخه‌بندی خودکار استفاده می‌شود.
- هر PR، یک changeset توصیف می‌کند: patch/minor/major.
- پس از merge، CI نسخه را bump و منتشر می‌کند.

### ۲۲-۴) مدیریت Breaking Changes

| قاعده / Rule                      | توضیح                                                                 |
| --------------------------------- | --------------------------------------------------------------------- |
| **هرگز breaking بدون major bump** | semver سخت‌گیرانه                                                     |
| **هشدار قبلی**                    | breaking change باید حداقل یک نسخه قبل اعلام شود (deprecation notice) |
| **codemod**                       | برای breaking های بزرگ، یک codemod مهاجرت ارائه می‌شود                |
| **فترة migration**                | حداقل ۲ نسخه برای مهاجرت قبل از حذف کامل                              |

---

## ۲۳) استراتژی دیپلوی پیشرفته / Advanced Deployment Strategy

### ۲۳-۱) استراتژی‌ها

| استراتژی              | توضیح                                                        | مناسب برای                  |
| --------------------- | ------------------------------------------------------------ | --------------------------- |
| **Canary Release** 🟢 | نسخه‌ی جدید ابتدا به درصد کوچکی از کاربران (۵٪ → ۲۵٪ → ۱۰۰٪) | Remote های پر ریسک          |
| **Blue/Green**        | دو محیط کامل؛ switch سریع بین آن‌ها                          | Shell (کم‌ریسک‌تر rollback) |
| **Feature Flags**     | قابلیت‌ها بدون deploy جدید فعال/غیرفعال شوند                 | آزمایش تدریجی، kill switch  |

### ۲۳-۲) Pipeline کامل / Full Pipeline (فاز ۴ و بعد)

> ⚠️ **در POC محلی (فاز ۰–۲) و استقرار اولیه (فاز ۳) این Pipeline را اجرا نمی‌کنند.** فاز ۳ فقط Build → Deploy دارد (بخش ۵). تکمیل مراحل زیر در **فاز ۴**:

هر Pipeline (Shell یا Remote) می‌تواند شامل این مراحل باشد , و متناسب با نیاز می باشد: (ترتیب مهم هست ولی الزامی برای پیاده سازی همه وجود ندارد)

```
┌─────┐    ┌──────┐   ┌─────────┐    ┌───────┐   ┌──────────┐    ┌─────────┐   ┌────────┐    ┌──────────┐    ┌────────┐
│Lint │──▶│ Unit │──▶│Type Chk │──▶│ Build │──▶│ Bundle   │──▶│Contract │──▶│ Deploy │──▶│ Smoke    │──▶│Promote │
│     │    │ Test │   │         │    │       │   │ Analysis │    │ Test    │   │ Staging│    │ Test     │    │  Prod  │
└─────┘    └──────┘   └─────────┘    └───────┘   └──────────┘    └─────────┘   └────────┘    └──────────┘    └────────┘
                                                                                                       │
                                                                                          Canary: 5%→25%→100%
                                                                                          (با rollback سریع)
```

### ۲۳-۳) Feature Flags در عمل

| کاربرد / Use Case | مثال                                     |
| ----------------- | ---------------------------------------- |
| **آزمایش تدریجی** | قابلیت جدید ابتدا برای ۱۰٪ کاربران فعال  |
| **Kill Switch**   | در صورت مشکل، بدون rollback فوری غیرفعال |
| **A/B Testing**   | مقایسه‌ی دو نسخه‌ی UI                    |
| **سناریوی TWA**   | قابلیت‌های موبایل‌خاص فقط در TWA فعال    |

> 📌 Feature flags در `@superapp/config` (بخش ۲۱) نگه‌داری می‌شوند؛ ارزیابی می‌تواند client-side یا server-side باشد.

---

## ۲۴) Observability / Monitoring & Logging

> 🎯 اگر چه «فعلاً monitoring مد نظر نیست» اما حداقل لاگ خطا برای پایداری production الزامی است. انتخاب: Azure Application Insights (هم‌اکنون روی Azure هستید).

> 🎯 **در آینده Sentry میتواند گزینه کامل تر و مناسب تری باشد**

### ۲۴-۱) لایه‌های Observability

| لایه / Layer       | ابزار / Tool                 | محتوا / Content                          |
| ------------------ | ---------------------------- | ---------------------------------------- |
| **Error Tracking** | Application Insights         | خطاهای runtime، unhandled exceptions     |
| **Performance**    | Application Insights         | page load، API latency، Remote load time |
| **Custom Events**  | Application Insights         | user actions، feature usage              |
| **Console**        | console.error (ساختار یافته) | dev/debug                                |

### ۲۴-۲) چی پکیج Observability

یک پکیج مشترک **`@superapp/observability`** (یا ادغام در `@superapp/api`) که API یکنواخت فراهم می‌کند:

```ts
// در هر Remote یا Shell
import { trackEvent, trackError, trackMetric } from "@superapp/observability";

try { ... }
catch (e) {
  trackError(e, { context: "food-checkout", remote: "food" });
}
```

**مزیت:** اگر در آینده از Application Insights به Sentry (یا OpenTelemetry) منتقل شدید، فقط این پکیج آپدیت می‌شود — Remote ها تغییری نمی‌کنند.

### ۲۴-۳) فازبندی Observability

| فاز                 | محتوا                                                  |
| ------------------- | ------------------------------------------------------ |
| **فاز ۰–۲ (PoC)**   | `console.error` — بدون Application Insights            |
| **فاز ۳ (استقرار)** | error tracking پایه (Application Insights)             |
| **فاز ۴**           | کامل: custom events، performance monitoring، dashboard |

> 📌 PoC محلی به monitoring نیاز ندارد. در شرکت: «فعلاً monitoring نه» = custom analytics نه، ولی error tracking از فاز ۳.

---

## ۱۶) نسخه‌بندی مستقل + هم‌راستاسازی توصیه‌شده / Independent Versioning with Recommended Alignment

> 🎯 **تصمیم مدیر تیم:** **rollback و نسخه‌بندی مستقلِ هر Remote، اولویت قطعی است.** در کنار آن، اگر ممکن باشد، دوست داریم همه‌ی Remote ها و Shell از **نسخه‌ی پکیج یکسان** استفاده کنند (شبیه مزیت monorepo). اما هرجا این دو در تضاد بودند، **استقلال rollback مقدم است.**

این تصمیم، مدل قدیمی «همگام‌سازی اجباری (Lockstep سخت)» را کنار می‌گذارد. Lockstep اجباری، rollback مستقل را ناامن می‌کرد (اگر Remote به بیلد قدیمی برگردد ولی مجبور به هم‌نسخه بودن با بقیه باشد، rollback عملاً ممکن نیست). به‌جای آن، سه لایه تعریف می‌شود:

| لایه                            | قاعده                                                                                                                 | ماهیت                  |
| ------------------------------- | --------------------------------------------------------------------------------------------------------------------- | ---------------------- |
| **۱. نسخه‌بندی/rollback مستقل** | هر Remote `package.json` مستقل و نسخه‌ی مستقل دارد و **هر لحظه به هر بیلد قبلی خود rollback می‌شود**                  | **تضمین‌شده (اولویت)** |
| **۲. هم‌راستاسازی توصیه‌شده**   | `@superapp/foundation` یک **مجموعه‌ی نسخه‌ی canonical** پیشنهاد می‌دهد؛ تیم‌ها تشویق (نه مجبور) به هم‌راستایی می‌شوند | توصیه (soft)           |
| **۳. انضباط سازگاری عقب‌رو**    | پکیج‌های مشترک (`@superapp/ui/auth/api/state`) در طول یک major، **سازگاری عقب‌رو (backward-compatible)** حفظ می‌کنند  | invariant الزامی       |

> 🔑 **نکته‌ی کلیدی که همه‌چیز را ممکن می‌کند:** چون پکیج‌های مشترک در Module Federation به‌صورت `singleton` بارگذاری می‌شوند، در runtime فقط **یک نسخه** فعال است. ⚠️ **اما این نسخه به‌صورت پیش‌فرض «بالاترین نسخه‌ی ثبت‌شده» است، نه لزوماً نسخه‌ی Shell/Host.** برای اینکه واقعاً نسخه‌ی Shell فعال بماند، به **قاعده‌ی سقف (`max-in-production.json`) + ترتیب دیپلوی Shell-اول** نیاز داریم (بخش ۱۶-۲). با آن تضمین، rollbackِ مستقلِ یک Remote زمانی امن است که پکیج‌های مشترک **در همان major عقب‌رو سازگار** باشند. پس دو چیز با هم استقلال rollback را ایمن می‌کنند: **انضباط سازگاری عقب‌رو + قاعده‌ی سقف** — نه lockstep و نه صرفِ singleton (رفع M-A).

### ۱۶-۱) تنها قید سخت: نسخه‌ی major مشترکِ React

React/react-dom استثناست: دو نسخه‌ی major متفاوت React نمی‌توانند به‌صورت singleton هم‌زیستی کنند. پس **تنها Lockstep واقعیِ باقی‌مانده، major نسخه‌ی React است** و بس. بقیه‌ی پکیج‌ها استقلال دارند.

```js
// federation config
shared: {
  react:       { singleton: true, strictVersion: true,  requiredVersion: "^18.0.0" }, // قید سخت (فقط React)
  "react-dom": { singleton: true, strictVersion: true,  requiredVersion: "^18.0.0" },

  // react-router-dom باید singleton باشد تا Router context بین Shell و Remote یکی بماند (M-D)
  "react-router-dom": { singleton: true, strictVersion: false, requiredVersion: "^6.0.0" },

  // پکیج‌های مشترک: singleton ولی tolerant → mismatch باعث crash نمی‌شود.
  // ⚠️ توجه: در MF به‌صورت پیش‌فرض «بالاترین نسخه‌ی ثبت‌شده» انتخاب می‌شود، نه لزوماً Host؛
  //     «Host-wins» فقط با قاعده‌ی سقف + ترتیب Shell-اول تضمین می‌شود (بخش ۱۶-۲، M-A).
  "@superapp/ui":          { singleton: true, strictVersion: false, requiredVersion: "^1.0.0" },
  "@superapp/auth":        { singleton: true, strictVersion: false, requiredVersion: "^1.0.0" },
  "@superapp/state":       { singleton: true, strictVersion: false, requiredVersion: "^1.0.0" },
  "@superapp/query":       { singleton: true, strictVersion: false, requiredVersion: "^1.0.0" },
  "@tanstack/react-query": { singleton: true, strictVersion: false, requiredVersion: "^5.0.0" },
  zustand:                 { singleton: true, strictVersion: false },
}
```

### ۱۶-۲) نقش `@superapp/foundation` — «توصیه»، نه «اجبار»

`@superapp/foundation` یک پکیج است که فقط **مجموعه‌ی نسخه‌ی پیشنهادی (canonical)** را نگه می‌دارد:

```
@superapp/foundation
├── canonical.json           ← مجموعه‌ی نسخه‌ی توصیه‌شده (هدف هم‌راستایی)
│   { "react": "18.3.1", "react-router-dom": "6.26.2", "@tanstack/react-query": "5.56.2",
│     "@superapp/ui": "1.5.0", "@superapp/auth": "2.1.0" }
│
├── compatibility.json        ← حداقل نسخه‌ی مجاز (کفِ سازگاری با Shell)
│   { "react": ">=18.0.0", "react-router-dom": ">=6.0.0", "@tanstack/react-query": ">=5.0.0",
│     "@superapp/ui": ">=1.4.0", "@superapp/auth": ">=2.0.0" }
│
└── max-in-production.json    ← 🆕 سقفِ نسخه = نسخه‌ی فعلاً مستقرِ Shell در prod
    { "@superapp/ui": "1.5.0", "@superapp/auth": "2.1.0",
      "@tanstack/react-query": "5.56.2", "react-router-dom": "6.26.2" }
    (به‌صورت خودکار توسط pipeline دیپلوی Shell به‌روزرسانی می‌شود)
```

#### چرا «سقف» لازم است؟ (رفع C2 — نقص forward-compatibility)

انضباط سازگاری عقب‌رو فقط **«کف»** را ایمن می‌کند، اما با `singleton` یک ریسک ظریفِ **forward-compat** باقی می‌ماند:

```
Shell در prod:  @superapp/ui@1.4.0  (فعال به‌صورت singleton — تحتِ قاعده‌ی سقف، نسخه‌ی Shell)
Remote-food بیلد شده با:  @superapp/ui@1.6.0  (از API جدیدِ 1.6 استفاده می‌کند)
→ در runtime، Remote کد 1.4 را از Host می‌گیرد → متد/prop جدیدِ 1.6 وجود ندارد → crash
```

یعنی یک Remote **هرگز نباید نسخه‌ی shared را بالاتر از نسخه‌ی مستقرِ Shell** انتخاب کند. قاعده:

> 🔒 **invariant دوطرفه:** `compatibility.json` ≤ نسخه‌ی Remote ≤ `max-in-production.json`
> (نه زیر کفِ سازگاری، نه بالاتر از نسخه‌ی فعالِ Shell در prod)

و **ترتیب دیپلوی** تضمین‌کننده است: **همیشه ابتدا Shell با نسخه‌ی جدید دیپلوی می‌شود**، سپس `max-in-production.json` به‌روز می‌شود، و تنها بعد از آن Remote ها اجازه‌ی bump به نسخه‌ی بالاتر را دارند.

**تفاوت اساسی با مدل قبلی (Lockstep سخت):**

| موضوع                 | مدل قدیم (Lockstep اجباری)                       | مدل جدید (استقلال + توصیه)                    |
| --------------------- | ------------------------------------------------ | --------------------------------------------- |
| منبع نسخه‌ی هر Remote | foundation (تحمیلی)                              | **`package.json` خودِ Remote** (مستقل)        |
| مکانیزم resolve       | نیازمند ابزار سفارشی برای «خواندن از foundation» | **`bun`/`pnpm` استاندارد** — بدون مکانیزم خاص |
| rollback مستقل        | ❌ عملاً ناممکن                                  | ✅ **تضمین‌شده**                              |
| نقش foundation        | اجبار                                            | **توصیه + ابزار هم‌راستاسازی**                |

> 📌 چون `package.json` هر Remote منبع رسمی resolve است، **هیچ مکانیزم غیراستانداردی لازم نیست** (رفع ابهام پیاده‌سازیِ نسخه‌ی قبلی). `foundation` صرفاً برای «چه نسخه‌ای توصیه می‌شود» و ابزارِ باز کردن PR خودکار به کار می‌رود.

### ۱۶-۳) گیت CI: سقف نه، کف

CI هر Remote، نه «هم‌نسخه بودن اجباری»، بلکه فقط **کفِ سازگاری** را چک می‌کند تا rollback مستقل حفظ شود:

| بررسی CI / CI Check                                                  | رفتار در صورت شکست                                                       |
| -------------------------------------------------------------------- | ------------------------------------------------------------------------ |
| major نسخه‌ی `react` با Shell یکی است؟                               | ❌ بیلد fail (تنها قید سخت)                                              |
| نسخه‌های `@superapp/*` ≥ `compatibility.json`؟ (کف)                  | ❌ بیلد fail (زیر کف سازگاری)                                            |
| نسخه‌های `@superapp/*` ≤ `max-in-production.json`؟ (سقف — 🆕 رفع C2) | ❌ بیلد fail (بالاتر از نسخه‌ی مستقرِ Shell → ریسک forward-compat crash) |
| نسخه با `canonical.json` هم‌راستاست؟                                 | ⚠️ فقط **هشدار** (warning) — بیلد fail نمی‌شود                           |

> این یعنی هر Remote آزاد است هر نسخه‌ای **بین کف و سقف** انتخاب کند یا به آن rollback کند؛ اما اجازه ندارد از **نسخه‌ی فعلاً مستقرِ Shell** جلو بزند (تا crash forward-compat رخ ندهد). اگر با `canonical` هم‌راستا نباشد، فقط یک هشدار غیرمسدودکننده می‌گیرد.
>
> 📌 **گردش عملی:** وقتی تیم پلتفرم `@superapp/ui@1.6.0` را منتشر می‌کند، Remote ها **هنوز نمی‌توانند** به ۱.۶ bump کنند تا زمانی که Shell با ۱.۶ دیپلوی شود و `max-in-production.json` به‌روز شود. این دقیقاً همان invariant «Shell اول» است که در بخش ۱۸-۴ هم آمده.

### ۱۶-۴) مزایا و بهای این مدل

| ✅ مزیت / Benefit                                        | ⚠️ بهای آن / Cost                                                                            |
| -------------------------------------------------------- | -------------------------------------------------------------------------------------------- |
| **rollback و نسخه‌بندی کاملاً مستقل هر Remote** (اولویت) | نیاز به انضباط «سازگاری عقب‌رو» در پکیج‌های مشترک                                            |
| resolve استاندارد `bun`/`pnpm` — بدون ابزار سفارشی       | ممکن است موقتاً نسخه‌های build-time متفاوتی در prod باشند (بی‌خطر با انضباط بالا + سقف نسخه) |
| هم‌راستایی به‌عنوان هدف، نه مانع سرعت تیم                | breaking change (major bump) پکیج مشترک همچنان نیازمند هماهنگی است                           |
| `singleton` React همیشه برقرار (تنها قید سخت)            | —                                                                                            |

### ۱۶-۵) گردش کار ارتقا و rollback / Upgrade & Rollback workflow

**ارتقا (توصیه‌شده، نه اجباری) — با رعایت ترتیب «Shell اول» (رفع C2):**

1. تیم پلتفرم `@superapp/ui@1.5.0` را منتشر و `canonical.json` را در `foundation` bump می‌کند.
2. **ابتدا Shell** با نسخه‌ی جدید دیپلوی می‌شود؛ سپس pipeline دیپلوی Shell به‌صورت خودکار `max-in-production.json` را به `1.5.0` به‌روز می‌کند (سقف بالا می‌رود).
3. حالا Pipeline `foundation` به تیم‌های صاحب Remote **نوتیفیکیشن/PR خودکار** می‌فرستد (پیشنهاد ارتقا).
4. هر تیم **در زمان دلخواه خود** PR را merge می‌کند؛ اجباری در کار نیست تا وقتی بین کف و سقف بماند.

**rollback مستقل (تضمین‌شده):**

1. اگر بیلد جدید `remote-food` مشکل داشت، ورودی manifest به بیلد قبلی همان Remote برمی‌گردد.
2. چون پکیج‌های مشترک در همان major عقب‌رو سازگارند، بیلد قدیمی `remote-food` با singleton فعلی Shell **بدون خطا** کار می‌کند.
3. اگر مشکل از یک major bumpِ پکیج مشترک بود (حالت نادر)، rollback باید **هماهنگ** (Shell + Remoteهای متأثر) انجام شود — این حالت به‌صراحت به‌عنوان استثنا پذیرفته می‌شود.

> 💡 **صداقت فنی:** artifactهای Remote ممکن است در پنجره‌ی زمانیِ بین ارتقاها با نسخه‌های build-time متفاوت در prod وجود داشته باشند. آن‌چه ایمنی را تضمین می‌کند، **singleton در runtime + سازگاری عقب‌رو** است، نه یکسان بودن اجباریِ همه‌ی نسخه‌ها.

---

## ۱۷) Service Worker + Push Notification / Push & Caching Subsystem

> 🎯 **تصمیم مدیر تیم:** Service Worker برای **FCM / Push Notification** لازم است (push بدون SW ممکن نیست) و در آینده اهمیت زیادی خواهد داشت. **اما کارکرد اپ در حالت آفلاین برای شرکت ارزشی ندارد** و از دامنه خارج است.

**پیامد این تصمیم بر معماری:**

| مؤلفه                                    | وضعیت                      | دلیل                                    |
| ---------------------------------------- | -------------------------- | --------------------------------------- |
| **Push (FCM) در SW**                     | ✅ در دامنه                | نیاز قطعی؛ push بدون SW کار نمی‌کند     |
| **Precache برای سرعت (performance)**     | ✅ در دامنه (اختیاری/مفید) | بارگذاری سریع‌تر تکراری، نه برای آفلاین |
| **Offline mode (کارکرد آفلاین اپ)**      | ❌ خارج از دامنه           | ارزشی برای شرکت ندارد                   |
| **Background Sync / صف mutation آفلاین** | ❌ حذف شد                  | چون آفلاین لازم نیست                    |
| **Runtime caching API برای کار آفلاین**  | ❌ حذف شد                  | داده‌ی آفلاین لازم نیست                 |

> ✅ **حسن جانبی:** حذف آفلاین‌مود، تناقض قبلی با توکنِ فقط-in-memory را نیز برطرف می‌کند (بخش ۲-۳) — دیگر لازم نیست session در حالت آفلاین بازیابی شود.

Service Worker **تماماً در Shell** پیاده‌سازی می‌شود (Remote ها درگیر نمی‌شوند — چون SW باید یکی باشد).

### ۱۷-۱) معماری Service Worker (بدون آفلاین)

```
┌─────────────────────────────────────────────────────────────┐
│                      Shell (Host)                           │
│                                                             │
│   ┌───────────────────────────────────────────────────────┐ │
│   │              Service Worker (یکی ، در Shell)         │  │
│   │                                                       │  │
│   │   ┌────────────────────────┐   ┌────────────────────┐ │  │
│   │   │ Precache (performance) │   │  Push (FCM)        │ │  │
│   │   │                        │   │                    │ │  │
│   │   │ - App Shell (HTML/JS)  │   │ - receive push     │ │  │
│   │   │ - Design System assets │   │ - show notification│ │  │
│   │   │ - فقط برای سرعت،      │   │ - click → route    │ │  │
│   │   │   نه آفلاین            │   │                    │ │  │
│   │   └────────────────────────┘   └────────────────────┘ │  │
│   │                                                       │  │
│   │   ❌ بدون Background Sync   ❌ بدون Offline data    │  │
│   └───────────────────────────────────────────────────────┘  │
│                                                              │
│   بیلد با Workbox (Google) — پیکربندی declarative           │
└──────────────────────────────────────────────────────────────┘
```

### ۱۷-۲) استراتژی‌های Caching (فقط برای سرعت، نه آفلاین)

> 📌 هدف این caching صرفاً **کارایی (بارگذاری سریع‌تر تکراری)** است — نه فعال‌کردن کار آفلاین. اگر شبکه نباشد، اپ کار نمی‌کند و این پذیرفته شده است.

| منبع / Resource                                      | استراتژی / Strategy                                       | دلیل                                                            |
| ---------------------------------------------------- | --------------------------------------------------------- | --------------------------------------------------------------- |
| App Shell (HTML, JS, CSS اصلی)                       | **Precache** (اختیاری)                                    | بارگذاری سریع‌تر در بازدید بعدی                                 |
| **`remoteEntry.js` ها**                              | **Network-first** (نه stale-while-revalidate — 🆕 رفع C3) | تضمین rollback فوری؛ دریافت همیشه‌ی آخرین ورودی manifest        |
| **chunkهای hash‌دارِ Remote** (`*.[contenthash].js`) | **Cache-first طولانی (immutable)**                        | چون نامشان hash دارد، امن برای کش دائمی؛ بدون تداخل با rollback |
| Design System assets (فونت، آیکون)                   | **Cache-first** طولانی                                    | تغییر نادر، کاهش latency                                        |
| API GET/POST/...                                     | **همیشه شبکه (network-only)**                             | آفلاین لازم نیست؛ سادگی و صحت داده                              |

> ⚠️ **نکته:** چون آفلاین در دامنه نیست، هیچ mutation در SW صف نمی‌شود و هیچ داده‌ی API برای مصرف آفلاین cache نمی‌شود. این پیچیدگی عمداً حذف شده است.

### ۱۷-۲-الف) حل تداخل کش SW با rollback مستقل (رفع C3)

> 🎯 در نسخه‌ی قبلی، `remoteEntry.js` با **stale-while-revalidate** کش می‌شد، در حالی‌که rollback = «تغییر ورودی manifest» است. این دو **تداخل** داشتند: هنگام rollback، SW ممکن بود نسخه‌ی **stale (خرابِ جدید)** `remoteEntry.js` را تا revalidate بعدی سرو کند → کاربرِ آنلاین همچنان بیلد معیوب را می‌دید.

راهکار ترکیبی (هر دو با هم اعمال می‌شوند):

| اقدام                                         | توضیح                                                                                                                                                                                                         |
| --------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **`remoteEntry.js` = network-first**          | همیشه ابتدا از شبکه گرفته می‌شود؛ کش فقط fallback اضطراری است. rollback بلافاصله دیده می‌شود.                                                                                                                 |
| **نام‌گذاری hash-based + versioned manifest** | خودِ `remoteEntry.js` یا chunkهای آن با `contenthash` نام‌گذاری می‌شوند و manifest به فایل نسخه‌دار اشاره می‌کند. rollback = تغییر اشاره‌گر manifest به hash قبلی → URL متفاوت → کش قبلی اصلاً درگیر نمی‌شود. |
| **`skipWaiting` + `clients.claim` کنترل‌شده** | هنگام دیپلوی/rollback، SW جدید فوراً فعال می‌شود تا کش قدیمی سرو نشود.                                                                                                                                        |

> ✅ **نتیجه:** rollback مستقلِ هر Remote (بخش ۱۶) اکنون **فوری و قطعی** است و هیچ کاربری — حتی کسی که دقیقاً موقع rollback آنلاین است — بیلد معیوب را نمی‌بیند.

### ۱۷-۳) Push Notification (آینده، ولی زیرساخت از الان)

> 📌 **تفکیک VAPID vs FCM (رفع M-F):** دو مسیر متمایز‌اند: (۱) **Web Push استاندارد با VAPID** — مستقیم با Push API مرورگر، بدون وابستگی به Google؛ (۲) **FCM** — لایه‌ی Google که برای اندروید/TWA یکپارچگیِ عمیق‌تری می‌دهد. **تصمیم پیش‌فرض:** Web Push استاندارد با **VAPID** برای وب؛ استفاده از **FCM** فقط اگر یکپارچگیِ عمیقِ اندروید/TWA لازم شد. این انتخاب باید قبل از فاز ۳ نهایی شود چون بر Backend و Service Worker اثر مستقیم دارد.

| مؤلفه / Component                  | توضیح                                                                    | زمان              |
| ---------------------------------- | ------------------------------------------------------------------------ | ----------------- |
| **VAPID keys**                     | کلیدهای عمومی/خصوصی برای web push؛ یک‌بار تولید، در Backend/Identity ثبت | فاز ۱ (زیرساخت)   |
| **Push Subscription**              | Shell هنگام لاگین، subscription کاربر را در Backend ثبت می‌کند           | فاز ۱ (زیرساخت)   |
| **Push Event Handler**             | SW پیام را می‌گیرد و notification نمایش می‌دهد                           | فاز ۳ (فعال‌سازی) |
| **Notification Click → Deep Link** | کلیک → باز شدن `/food/orders/123` در Shell                               | فاز ۳             |

**جریان push:**

```
┌──────────┐   push    ┌────────────┐   web push   ┌─────────────────┐
│ Backend  │─────────▶│  FCM       │─────────────▶│ Shell Service   │
│ (event)  │           │ (Google)   │              │ Worker          │
└──────────┘           └────────────┘              └────────┬────────┘
                                                            │
                                                   ┌────────▼────────┐
                                                   │ Notification    │
                                                   │ (click → route) │
                                                   └─────────────────┘
```

> 💡 **نکته‌ی TWA:** در TWA اندروید، push از طریق **FCM مستقیم** کار می‌کند — همان کد web push بدون تغییر. این یکی دیگر از مزایای انتخاب TWA (نه native app) است.

### ۱۷-۴) اشتراک Remote ها در Push

گاهی یک Remote می‌خواهد notification بفرستد (مثلاً Remote:Food سفارش جدید را اطلاع می‌دهد):

- Remote از طریق یک **API مشترک push** در Backend، رویداد را ثبت می‌کند.
- Backend با FCM هماهنگ می‌شود و push را به کاربر مربوطه می‌فرستد.
- **Remote هرگز مستقیماً push نمی‌فرستد** — همیشه از طریق Backend. این یک قاعده‌ی معماری است.

> قاعده: **Frontend فقط مصرف‌کننده‌ی push است (never sender).** ارسال همیشه از سمت Backend.

---

> **گام بعدی پیشنهادی:** تأیید این سند → **شروع PoC محلی (فاز ۰):** Nx Monorepo (`bunx nx init` با `bun`) + Shell/Remote روی localhost با **Module Federation 2.0 روی Rspack** (`NxModuleFederationPlugin` + `module-federation.config.ts`؛ shared شامل `react-router-dom` و `@tanstack/react-query` به‌صورت singleton) — **بدون Azure، بدون Verdaccio**. جزئیاتِ گام‌به‌گام در `SuperApp-Implementation-Progress.md`.

_تهیه‌شده برای ارائه به تیم فنی — آماده‌ی بازبینی و بحث._

</div>
