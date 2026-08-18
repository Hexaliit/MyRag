# 📘 راهنمای آموزش تیم سوپر اپ (SuperApp)

## فهرست دانش فنی و مفاهیم مورد نیاز بر اساس معماری

> **هدف:** این سند، فهرست کامل تکنولوژی‌ها، ابزارها و مفاهیم معماری‌ای است که هر عضو تیم (توسعه‌دهنده، طراح، DevOps) باید برای پیاده‌سازی سوپر اپ با آن‌ها آشنا شود.  
> **منبع:** بر اساس `Architecture-Proposal-SuperApp2.md` و `IMPLEMENTATION-PROGRESS.md`.  
> **نحوه استفاده:** این فایل یک نقشه‌ی راه یادگیری است. ابتدا بخش «پیش‌نیازهای ضروری» را همگی مسلط شوند، سپس بقیه را به ترتیب یا به صورت موازی پیش ببرند.

---

## 🧩 سطح ۱: پیش‌نیازهای ضروری (همه اعضا، پیش از شروع کد)

> تسلط بر این موارد، شرط لازم برای درک صحیح معماری و جلوگیری از خطاهای اساسی است.

| موضوع                                   | تکنولوژی / مفهوم                                                                     | منابع یادگیری پیشنهادی                                                                                                                                                                      | میزان تسلط مورد نیاز                                                                                                                         |
| --------------------------------------- | ------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------- |
| **مدیریت مونو ریپو**                    | **Nx** (نسخه ۲۰ به بالا)                                                             | - مستندات رسمی Nx (بخش Workspace, Caching, Project Graph) <br> - بخش ۴-۱ از `IMPLEMENTATION-PROGRESS.md`                                                                                    | توانایی خواندن `project.json`، درک `nx.json`، اجرای `nx affected` و `nx graph`                                                               |
| **استراتژی ریپو: Monorepo vs Polyrepo** | Nx Monorepo (مسیر اصلی) با مرزبندی tag-based، Polyrepo (فقط در صورت نیاز به ACL سخت) | - بخش ۲-۲ از سند معماری <br> - بخش ۴-۱ و ۷-۱ از سند پیاده‌سازی <br> - مستندات `@nx/enforce-module-boundaries` و `git filter-repo`                                                           | درک تفاوت، زمان استفاده از هر کدام، نحوه‌ی مهاجرت با `git filter-repo` و نقش `CODEOWNERS`                                                    |
| **پیکربندی پایه TypeScript**            | `tsconfig.base.json` (ریشه)                                                          | - بخش ۴-۳ از `IMPLEMENTATION-PROGRESS.md` <br> - مستندات TypeScript (compilerOptions)                                                                                                       | تنظیم `compilerOptions` پایه برای مونو ریپو (moduleResolution, jsx, paths)                                                                   |
| **باندلر مدرن**                         | **Rspack** (نسخه ۱.۱)                                                                | - مستندات Rspack (مقایسه با Webpack) <br> - پیکربندی در `rspack.config.js` از بخش ۴-۵                                                                                                       | درک `entry`, `output`, `publicPath: auto` و نحوه کار با پلاگین‌های Nx                                                                        |
| **ماژول فدریشن (MF 2.0)**               | **`@module-federation/enhanced`**                                                    | - مستندات رسمی Module Federation 2.0 <br> - بخش ۲-۱ و ۴-۵ از سند معماری                                                                                                                     | تشخیص `Host` و `Remote`، تنظیم `shared`، مفهوم `singleton` و `strictVersion`                                                                 |
| **مدیریت state سمت کلاینت**             | **Zustand**                                                                          | - مستندات Zustand (ساخت store، selector) <br> - بخش ۲-۴ و ۵-۲ از سند پیاده‌سازی                                                                                                             | پیاده‌سازی یک store ساده، استفاده از `create` و `selectors`                                                                                  |
| **مدیریت state سمت سرور**               | **TanStack Query (React Query)**                                                     | - مستندات رسمی (بخش‌های `useQuery`, `useMutation`, `QueryClient`) <br> - بخش ۸ و ۵-۳ از سند                                                                                                 | درک `staleTime`, `cacheTime`, `queryKey`, و `invalidation`                                                                                   |
| **مسیریابی در React**                   | **React Router DOM (v6)**                                                            | - مستندات React Router v6 (Route, BrowserRouter, useNavigate) <br> - بخش ۲-۵ و ۵-۴                                                                                                          | تفاوت مسیریابی در Shell و Remote، استفاده از `relative routes`                                                                               |
| **استایل‌دهی با Tailwind**              | **Tailwind CSS v4 + `@theme`**                                                       | - مستندات Tailwind v4 (بخش `@theme`, `@source`) <br> - بخش ۷ و ۴-۷ از سند                                                                                                                   | توانایی استفاده از `@theme` برای تعریف توکن‌ها، کار با `logical properties` (`ps-*`, `pe-*`, `start-*`)                                      |
| **توسعه‌ی ایزوله کامپوننت**             | **Storybook**                                                                        | - مستندات Storybook (نوشتن stories، decorators) <br> - بخش ۵-۷ از سند پیاده‌سازی                                                                                                            | رندر کامپوننت‌ها در دو حالت RTL و LTR، نوشتن `globalTypes` برای جهت                                                                          |
| **انواع و اینترفیس‌ها + اعتبارسنجی**    | **TypeScript (5.5+) + Zod**                                                          | - مستندات TypeScript (Generics, Utility Types) <br> - مستندات Zod (schema declaration, parse, safeParse) <br> - فایل‌های `@superapp/types` و کاربرد Zod در اعتبارسنجی داده‌های API و فرم‌ها | تعریف `User`, `Permission`, `MenuItem` با TypeScript و ساخت Schemaهای Zod برای اعتبارسنجی ورودی‌ها (مثلاً داده‌های دریافتی از API یا فرم‌ها) |

---

## 🛠️ سطح ۲: هسته‌ی توسعه (پس از پیش‌نیازها، به ترتیب اولویت)

> این مباحث برای نوشتن کدهای اصلی پروژه (Shell و Remote اول) لازم است.

| موضوع                                  | تکنولوژی / مفهوم                                                  | منابع یادگیری                                                                          | زمان تخمینی                                                                                    |
| -------------------------------------- | ----------------------------------------------------------------- | -------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------- |
| **پیکربندی Nx برای MF**                | `NxModuleFederationPlugin`, `NxModuleFederationDevServerPlugin`   | - بخش ۴-۵ و ۴-۶ از `IMPLEMENTATION-PROGRESS.md` <br> - مستندات `@nx/module-federation` | ۱ روز                                                                                          |
| **اجرا و تأیید فاز ۰**                 | دستورات `nx serve shell`, `nx graph`, `nx build`                  | - بخش ۴-۸ از سند پیاده‌سازی                                                            | آشنایی با نحوه‌ی اجرای همزمان Shell و Remoteها، بررسی گراف وابستگی                             |
| **احراز هویت (Mock + واقعی)**          | OIDC (Authorization Code Flow + PKCE)، JWT، refresh token، CSRF   | - بخش ۲-۳ و ۵-۱ از سند <br> - مستندات کتابخانه‌ی OIDC (مثلاً `oidc-client-ts`)         | ۲ روز                                                                                          |
| **RBAC و منوی پویا**                   | کنترل دسترسی مبتنی بر نقش، تقاطع `permissions` و `manifest`       | - بخش ۶-۳ از سند پیاده‌سازی                                                            | پیاده‌سازی منوی دینامیک که بر اساس مجوزها و موجود بودن Remoteها در manifest نمایش داده می‌شود. |
| **مدیریت خطا و تاب‌آوری (Resilience)** | Error Boundaries، Suspense، Retry/Backoff، `errorLoadRemote` hook | - بخش ۱۸ و ۶-۱ از سند <br> - مستندات React (Error Boundaries)                          | ۱ روز                                                                                          |
| **کامپوننت‌های Variant دار**           | `cva` (class-variance-authority) و `tailwind-merge`               | - مستندات `cva` و `clsx` <br> - بخش ۴-۷ از سند                                         | ۰.۵ روز                                                                                        |
| **توسعه‌ی Adaptive (موبایل/دسکتاپ)**   | `useViewport`، breakpoint `768px`، پرایمیو `DataList`/`DataTable` | - بخش ۱۱ و ۶-۲ از سند                                                                  | ۱ روز                                                                                          |
| **Mock API با MSW**                    | MSW v2 (Service Worker برای intercept کردن درخواست‌ها)            | - بخش ۵-۶ از سند پیاده‌سازی <br> - مستندات MSW                                         | راه‌اندازی MSW در bootstrap، نوشتن `handlers` برای APIهای نمونه                                |
| **تست واحد و کامپوننت**                | Vitest + Testing Library                                          | - مستندات Vitest و Testing Library <br> - بخش ۱۹ از سند معماری                         | ۱ روز                                                                                          |
| **مدیریت رویدادهای Auth**              | Event Bus (تایپ‌سیف) برای ارتباط Shell ↔ Remote                   | - بخش ۲-۳ (`authEvents`) و ۵-۱ از سند پیاده‌سازی                                       | ۰.۵ روز                                                                                        |
| **اعتبارسنجی production build**        | دستورات `nx run-many -t build`, `serve`/`preview`                 | - بخش ۶-۴ از سند پیاده‌سازی                                                            | توانایی بیلد کردن همه‌ی پکیج‌ها و اجرای preview برای تأیید نهایی قبل از استقرار                |

---

## 🔁 سطح ۳: مباحث موازی و تخصصی (قابل تقسیم بین اعضای تیم)

> این مباحث برای توسعه‌ی Remoteهای جدید، استقرار و مقیاس‌پذیری ضروری هستند. هر عضو می‌تواند یک یا دو حوزه را عمیق‌تر بیاموزد و بقیه را در سطح آشنایی داشته باشد.

### گروه A: استقرار و زیرساخت (DevOps / Platform)

| موضوع                                 | تکنولوژی / مفهوم                                                                         | منابع                                              |
| ------------------------------------- | ---------------------------------------------------------------------------------------- | -------------------------------------------------- |
| **CI/CD با Azure DevOps**             | Pipelines (YAML)، `nx affected -t build`                                                 | بخش ۵ و ۷-۳ از سند پیاده‌سازی                      |
| **مدیریت بسته با Azure Artifacts**    | `.npmrc`، احراز هویت `bun`/`pnpm` با فید                                                 | بخش ۴ و ۷-۲ از سند                                 |
| **سرویس‌دهی استاتیک و CDN**           | Azure Static Web Apps / App Service، هدرهای کش                                           | بخش ۳ و ۱۷ از سند                                  |
| **امنیت (CSP, SRI, Trusted Domains)** | هدرهای CSP مبتنی بر hash، `script-src`, `connect-src`                                    | بخش ۲۰ و ۷-۸ از سند                                |
| **Dev Shell + Override محلی**         | Query parameter (`?__remote=...`) و localStorage برای override کردن Remote در محیط توسعه | بخش ۷-۶ از سند پیاده‌سازی و بخش ۱۰-۱ از سند معماری |
| **API Gateway (اختیاری)**             | مسیریابی درخواست‌ها، CORS متمرکز، توکن‌سازی                                              | بخش ۲-۳ و ۷-۵ از سند (در صورت نیاز به Gateway)     |

### گروه B: تجربه کاربری و طراحی (UI/UX / Design System)

| موضوع                            | تکنولوژی / مفهوم                      | منابع                |
| -------------------------------- | ------------------------------------- | -------------------- |
| **سیستم طراحی (Design Tokens)**  | CSS Variables از `@theme` در Tailwind | بخش ۷ و ۴-۷ از سند   |
| **تست بصری (Visual Regression)** | Chromatic + Storybook                 | بخش ۵-۷ و ۸-۵ از سند |
| **دسترسی‌پذیری (Accessibility)** | axe-core، WCAG 2.1                    | بخش ۱۹-۲ از سند      |

### گروه C: ارتباط با بک‌اند و API

| موضوع                      | تکنولوژی / مفهوم                        | منابع                |
| -------------------------- | --------------------------------------- | -------------------- |
| **قرارداد API با OpenAPI** | `openapi-typescript`، تولید type خودکار | بخش ۲۱ از سند معماری |
| **Contract Testing**       | Pact (مصرف‌کننده‌محور)                  | بخش ۱۹-۳ از سند      |

### گروه D: موبایل و PWA

| موضوع                          | تکنولوژی / مفهوم                                               | منابع               |
| ------------------------------ | -------------------------------------------------------------- | ------------------- |
| **Service Worker (برای Push)** | Workbox، `network-first`, `cache-first`                        | بخش ۱۷ و ۷-۷ از سند |
| **Web Push (VAPID / FCM)**     | VAPID keys، subscription management                            | بخش ۱۷-۳ از سند     |
| **TWA (Android)**              | `manifest.json`, `assetlinks.json`، بسته‌بندی برای Google Play | بخش ۱۱-۴ از سند     |

### گروه E: نظارت و پایش

| موضوع                            | تکنولوژی / مفهوم           | منابع                |
| -------------------------------- | -------------------------- | -------------------- |
| **Error Tracking & Performance** | Azure Application Insights | بخش ۲۴ از سند معماری |
| **لاگ‌گیری ساختاریافته**         | `@superapp/observability`  | بخش ۲۴-۲ از سند      |

### 🧭 آینده‌نگر: پکیج‌های موکول‌شده (برای فاز ۴)

این پکیج‌ها در حال حاضر در فازهای ۰ تا ۲ پیاده‌سازی نمی‌شوند، اما برای فازهای بعدی طراحی شده‌اند. بهتر است تیم از الان با هدف آن‌ها آشنا باشد:

| پکیج                      | کاربرد                                                                  | زمان پیاده‌سازی |
| ------------------------- | ----------------------------------------------------------------------- | --------------- |
| `@superapp/template`      | CLI scaffolder برای ساخت Remote جدید با تمام پیکربندی‌های استاندارد     | فاز ۴           |
| `@superapp/config`        | مدیریت متمرکز environment variables و feature flags                     | فاز ۴           |
| `@superapp/observability` | لایه‌ی انتزاع برای لاگ‌گیری و نظارت (Application Insights, Sentry, ...) | فاز ۴           |

---

## 📚 منابع کلی برای مطالعه

علاوه بر بخش‌های ارجاع‌داده‌شده، اسناد رسمی زیر برای تمامی اعضا مفید است:

- **Nx:** [nx.dev](https://nx.dev) (به‌ویژه بخش‌های «Concepts» و «Recipe: Module Federation with Rspack»)
- **Module Federation 2.0:** [module-federation.io](https://module-federation.io)
- **Rspack:** [rspack.dev](https://rspack.dev) (مقایسه با Webpack)
- **Tailwind v4:** [tailwindcss.com](https://tailwindcss.com) (نسخه‌ی بتا و مستندات `@theme`)
- **TanStack Query:** [tanstack.com/query](https://tanstack.com/query)
- **React Router:** [reactrouter.com](https://reactrouter.com)
- **Zustand:** [github.com/pmndrs/zustand](https://github.com/pmndrs/zustand)
- **Storybook:** [storybook.js.org](https://storybook.js.org)
- **Zod:** [zod.dev](https://zod.dev) (برای اعتبارسنجی داده‌ها و ساخت schema)
- **MSW:** [mswjs.io](https://mswjs.io) (برای mock کردن API)
- **Vitest:** [vitest.dev](https://vitest.dev) (تست سریع)
- **Testing Library:** [testing-library.com](https://testing-library.com) (تست کامپوننت‌های React)
- **Workbox:** [developer.chrome.com/docs/workbox](https://developer.chrome.com/docs/workbox) (مدیریت Service Worker)

---

## ✅ چک‌لیست تسلط فردی (نسخه‌ی تکمیل‌شده)

هر عضو تیم باید بتواند به سوالات زیر پاسخ دهد:

- [ ] تفاوت `compatibility.json` با `max-in-production.json` چیست؟
- [ ] چرا `react-router-dom` باید `singleton: true` داشته باشد؟
- [ ] اگر یک Remote نتواند `remoteEntry.js` را بارگذاری کند، چه اتفاقی می‌افتد و چه مکانیزمی آن را مدیریت می‌کند؟
- [ ] چگونه در Tailwind v4 یک توکن رنگی جدید به سیستم اضافه می‌کنیم؟
- [ ] فرق `useGlobalStore` (Zustand) با `useQuery` (TanStack Query) در چه سناریوهایی مشخص می‌شود؟
- [ ] چرا در `remote-food/global.css` از `@import "tailwindcss/preflight.css"` استفاده نمی‌کنیم؟
- [ ] چه زمانی از Zod برای اعتبارسنجی استفاده می‌کنیم و چه تفاوتی با TypeScript دارد؟
- [ ] **تفاوت Nx Monorepo با Polyrepo چیست؟ چه زمانی باید به Polyrepo مهاجرت کرد و چرا مسیر اصلی Nx Monorepo است؟**
- [ ] چگونه می‌توان یک Remote را در محیط Dev Shell با استفاده از `?__remote=...` override کرد و این کار چه مزیتی دارد؟
- [ ] نقش MSW در توسعه‌ی محلی چیست و چطور از آن برای شبیه‌سازی API استفاده می‌کنیم؟
- [ ] منوی پویا و RBAC چگونه بر اساس `permissions` و `manifest` پیاده‌سازی می‌شود؟
