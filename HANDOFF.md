# Handoff: Bushi Ban work (cloud session, 17 Nov 2026)

Everything from the cloud session is on the branch `ccr-8ea92446-zlpuye` of `thealrightkid1/suheerb`. `main` (the personal website) was never touched, and no pull request was opened.

**Heads-up:** this repository is **public**, so everything on this branch can be seen by anyone. To change that: GitHub → Settings → General → Danger Zone → Change visibility.

## Where things stand

**Current goal:** a **marketing website (front end only)** with a Log in button. The client's developer will connect that button to one login for the existing systems.

Two earlier directions were built first. They're finished, but they are no longer the plan.

| Folder | What it is | Status |
| --- | --- | --- |
| `bushiban-website/` | The marketing site | **Current deliverable.** Waiting for decisions (below). |
| `bushiban-platform/` | A full replacement app for the three existing systems | Finished and tested, but on hold. A reference for the developer, or delete it. |
| `dezify-hub/` | The first idea: a proxy that showed the three existing sites behind one login | Superseded. Safe to delete. |

## The existing systems

Three ASP.NET Core apps on IIS, all in development, built by Dezify for Bushi Ban International:

- **CRM:** `https://dezify.bestautomation.top/`
- **Attendance kiosk:** `https://dezify.attendance.bestautomation.top/`. The login we were given was rejected ("Invalid login attempt"), so this app was never seen from the inside.
- **Registration kiosk:** `https://dezify.registration.bestautomation.top/`

**Logins:** the user shared logins in the chat. They aren't stored anywhere in this repo, and the user plans to change them.

**What we learned about these apps:**
- All three send `X-Frame-Options: DENY`, so none of them can be embedded in another page.
- They were mapped page by page, read only. Nothing was submitted or changed. The full write-up is in `bushiban-platform/docs/ORIGINAL-SYSTEM.md`.
- About a third of the CRM is placeholder: Fee Management, Social and Event leads, Settings and Help are "Coming soon" pages. Email, SMS and Calendar are demo templates filled with fake data.
- **Payroll** (mentioned at the start) doesn't exist in any of the three apps.

## 1. Marketing site: `bushiban-website/`

**Build:** a single page in plain HTML, CSS and JavaScript, with no build step. The README covers everything below in more detail.

**Connecting it:** the developer only edits `assets/js/config.js`. It sets `loginUrl` (every Log in button), `trialFormEndpoint` (the trial-class form sends its JSON there) and `phone`.

**Design:**
- It follows the **Wix reference site** the user gave: `https://headless-mnteqlhiyzx-bushiban-1408.wix-site-host.com/directions/dojo/index.html`. Barlow Condensed and DM Sans fonts, square corners, hairline dividers, a scrolling ticker, a tilted red "Yes, I can." stamp and a giant footer wordmark.
- Colours follow the **60-30-10 rule**:

| Share | Colour | Used for |
| --- | --- | --- |
| 60% | Paper `#F6F3EE` | Page background and light sections |
| 30% | Ink `#151515` | Header, hero, dark sections, headlines, footer |
| 10% | Red `#C72E26` | Buttons and highlights only |

**Sections:** hero, ticker, stats, 10 programs with filters, story, "Why Bushi Ban", belt journey, three steps, photo gallery, 10 locations (7 in Texas, 3 in Connecticut), trial form, FAQ, call to action and footer. Log in appears in the header, in a Members box and in the footer. There's also search-engine setup (Schema.org data, robots.txt, sitemap).

**Tested:** at desktop and phone sizes, with no errors. The form checks, the hide-parent-field-for-adults rule, the menu and the filters all work.

**Taken from the Wix reference:**
- **Assets:** 8 photos, the white logo and the phone number 281-479-7979.
- **Headlines and phrases, word for word:**
  - "Real kids. Everyday wins."
  - "Yes, I can. The Bushi Ban spirit"
  - "Room to grow. People who care."
  - "Confidence starts here"
  - "Their afternoon. Full of possibility."
  - "School's out. We're in."
  - "Care with purpose"
  - "A chance to step forward"
  - "Developing young leaders."
  - "The Bushi Ban community"
  - "More moments. More to grow into."
- **Lightly reworded:** "Let's find your place.", "A special place to grow.", and the "forty years" and Student Leadership paragraphs.
- **Not taken:** the code is original. Nothing was copied from the Wix site's HTML or CSS.

**Decisions waiting on the user:**
1. **The copy:** keep it close to the Wix site, keep the style but rewrite the words, or make the design more distinct as well.
2. **Free trial:** whether the trial class is free. If it is, the button should say "Book a free trial class".
3. **Addresses:** street addresses for the 9 schools without one. Only Pasadena's (7770 Spencer Highway, TX 77505) is known.
4. **Program blurbs:** confirm the ones written from program names (Yo-Kung-Chi, Summer Camp, After School).
5. **Photos:** confirm they're cleared for marketing use, and confirm the domain (`bushiban.com` is a placeholder in the meta tags, robots.txt and sitemap).

## 2. Replacement platform: `bushiban-platform/` (on hold)

**What it is:** one ASP.NET Core 8 MVC app on SQL Server (SQLite also works for local runs) that replaces all three existing apps. It has:
- **CRM:** leads with a pipeline board and follow-ups, students, and a registrations inbox.
- **Attendance:** taking attendance per class, plus a PIN and name-search check-in kiosk.
- **Registration kiosk:** six forms with their full legal wording and signature pads, plus a form builder.
- **Academics:** programs and schedules, ranking systems, and belt promotions.
- **Billing:** contracts, monthly invoices, late fees and payments, posted to accounting automatically.
- **Accounting:** chart of accounts, vouchers, fiscal years, and the four financial reports.
- **Communications:** email and SMS with templates.
- **Admin:** calendar, users and permissions (the original 57 permissions and 6 presets), branches, audit log and profile.

**Data and rules:**
- Each branch only sees its own data. All 11 branches are seeded with the reference data copied from the live system.
- Deleted records can be restored, and every change is logged.

**State:**
- **Tests:** 96 automated tests pass.
- **Database:** the migration (`Data/Migrations/Initial`) is current, with no model changes pending.
- **Docs:** `docs/DEVELOPER-GUIDE.md` (how to run it, conventions, demo logins) and `docs/ORIGINAL-SYSTEM.md`.

**Changed from the original data:** the bank account "Meezan Bank Ltd" (developer test data) was renamed "Operating Bank Account". Real staff emails from the live CRM were **not** copied in.

**Running it locally:** from `src/BushiBan.Web`, with the .NET 8 SDK installed, run in Development mode with `Database__Provider=Sqlite` and `ConnectionStrings__Default="Data Source=bb.db"`. Sign in as `admin@bushiban.local` with password `Admin!2026`. Those are demo logins that exist only on a local copy.

## 3. Hub proxy: `dezify-hub/` (superseded)

A small Node server that signed in to the three sites on the server side and showed them in one shell. It worked for CRM and Registration; the Attendance login failed. Kept only for history.

## Research notes: is there an existing open-source system?

- **Open source:** nothing mature is built specifically for martial arts schools. The small or old projects are Open Black Belt, Budo Systems (GitLab), OpenDojo, and Guru (the open-source version of StudioJoy). The strongest general open-source option is ERPNext with its Frappe Education app, which needs custom work for belt ranks.
- **Commercial leaders:** **Kicksite** (martial-arts-only since 2007) and **Gymdesk** lead the reviews at 4.8 stars on Capterra. **Spark Membership** is strongest for marketing and lead generation. **Zen Planner** is the older, broad platform for large operators.
