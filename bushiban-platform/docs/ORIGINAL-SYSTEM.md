# What the original apps do

This is what the three original apps did as of November 2026, mapped page by page from the live sites. Nothing was submitted or changed there.

- **CRM:** `dezify.bestautomation.top`. 105 pages were mapped while signed in as a branch manager.
- **Registration kiosk:** `dezify.registration.bestautomation.top`. All 6 forms were mapped.
- **Attendance kiosk:** `dezify.attendance.bestautomation.top`. The login we were given was rejected, so this one is known only from the CRM's attendance screens and permissions.

All three are ASP.NET Core apps on IIS that share one database. Data is split by branch. The CRM calls itself version 0.2.0-alpha.

The rebuild covers everything below. Anything marked **Placeholder** was a "Coming soon" page or a template demo with fake data in the original. Those are built for real in the rebuild.

## Branches and users

- **Branches:** 11 of them. Pasadena (Deer Park) TX 77505, Friendswood TX 77546, Houston Clear Lake TX 77058, League City TX 77573, Missouri City TX 77459, Pearland TX 77584, Seabrook TX 77586, Seymour CT 06483, Shelton CT 06484, Southbury CT 06488, and a Demo Branch. Each branch is tied to a city.
- **Schools** group branches.
- **Users:** each user has a user name, email, permission preset and branch. Admin screens let you edit a user, reset a password, and set permissions per feature with Select all, Clear all and per-group toggles.
- **Presets:** SystemAdmin, Manager (52 permissions), Accountant (15), Receptionist (10), AttendanceUser (2) and RegistrationUser (2). Each branch has three accounts: a manager, an attendance kiosk login and a registration kiosk login.
- **Permissions:** 57 in total, in the groups Students & CRM, Attendance, Academics & Belt Ranks, Accounting & Finance, Administration and Kiosks. The rebuild keeps every one under the same name: see `Security/Permissions.cs`.

## CRM

### Leads

| Screen | Details |
| --- | --- |
| List | Columns: Full Name, Phone, Email, City, Age, Rank, Program, Inquiry Date, Prospect Phase, Active, Action. Has advanced filters and a delete confirmation. |
| Create / Edit | Sections: Personal Details, Contact & Address, Program Interest, Notes & Source, Social Media. |
| Social / Event | **Placeholder.** |

Lead fields:

- **Personal:** Active, Title\*, First\*, Last\*, Father's Name, Mother's Name, Date of Birth, Age Group (Pre-School, Kids, Teens, Adults), Gender (Male, Female, Other), Belt Size.
- **Contact:** Phone 1 label and number, Phone 2 label and number, Email 1 label and address, Email 2 label and address, Address 1, Address 2, Country, State and City (each pick narrows the next), Zip.
- **Program:** Interested Program, Rank (if any), Inquiry Date\*, Prospect Phase\*.
- **Notes:** Medical Concerns, Additional Information, Marketing Source and Detail, Meeting and Meeting Detail.
- **Social:** Facebook, Instagram, LinkedIn, Pinterest, Twitter, Website.

Prospect phases, in order:

1. Hasn't Set Appointment
2. Set Appointment
3. In Trial - Took First Class
4. 3rd Class Progress Check
5. Presented Program - Waiting for Commitment
6. Presented Program - Denied Enrollment
7. Enrolled
8. Stop Following Up

### Students

| Screen | Details |
| --- | --- |
| List | Columns: Full Name, Phone, Email, City, Age, Rank, Program, Enrollment, Active, Action. |
| Create / Edit | Same fields as a lead, but most are required. Adds the sections and fields below. |
| Fee Management | **Placeholder.** |

Extra student fields:

- **Program & Enrollment:** Program\*, Rank\*, plus read-only fee fields filled from the program's fee structure (Admission, Security, Monthly), Discount\*, Net Payable, Enrollment Date\*.
- **Communication:** subscriber checkboxes for Email 1, Email 2, Phone 1, Phone 2 and Postcard.
- **Numbering:** student numbers are unique and never reused.

### Registrations

The inbox of kiosk submissions.

- **Columns:** Student, Contact, Form, Submitted, Status, Linked To, Phone, Email, Action.
- **Actions:** link, archive, discard, and "Create Student". That last one pre-fills the student from the form, and staff confirm the program, rank and belt size before saving.

### Communications and calendar

- **Email, SMS, Calendar:** **Placeholder.** These were UI templates with fake data, with no sending and no saved events. The calendar's event form has Title, Start, End, a Label (Personal, Business, Family, Holiday, ETC) and a Description.

## Attendance

| Screen | Details |
| --- | --- |
| Records list | Columns: Student, Date, Check-In Time, Method, Marked By, Confidence, Action. Has filters. |
| Add record | Filter by program, then pick a Student\*, Date\* and Check-In Time\*. |
| Kiosk app | Separate app for the "Attendance Kiosk Terminal" accounts. We couldn't sign in. The "Method" and "Confidence" columns suggest automated check-in, likely face recognition. |

## Academics

- **Programs** (called Courses in the code): Name\*, Description, Fee Structure, Active, an ordered list of Ranking Systems, and a Class Schedule. The schedule is either one start time and duration for selected days, or different times per day.
  - Seeded programs: Pre-K Martial Arts, Youth Martial Arts, Adult Martial Arts, Power Kickboxing, Yo-Kung-Chi, Tai Chi, MMA, Jiujitsu, After School Martial Arts Program, Summer Camp.
- **Ranking Systems:** Name\*, Description, Active, and ordered ranks, each with an index, a rank and a duration in days.
  - The seeded "Standard System" runs White, Gold, Orange, Green, Purple, Blue, Red, Red/Brown, Brown, Brown/Black, Oreo, Black, then 1st to 10th Dan. Each step is 90 days.
- **Ranks:** Name. The list shows how many systems, students and leads use each rank. Names are unique within a branch.
- **Belt Sizes:** Name, Size in inches, Active. Eleven sizes, from Size 000 (Child S, 83") to Size 8 (158").
- **Fee Structures:** Name, Admission Fee, Security Fee, Monthly Fee, Active. Seeded: "Standard Martial Arts Fee" at $50, $100 and $80.

## Accounting

- **Main Accounts:** Code (e.g. 10-01), Name, Type (Assets, Liabilities, Equity, Revenue, Expenses), Active. 13 seeded.
- **Sub Accounts:** Code (10-01-01), Name, Main Account. 51 seeded.
- **Detail Accounts:** Code (10-01-01-001), Name, Sub Account, Branch. 54 seeded. The full chart is in `Data/Seed/ReferenceData.cs`.
- **Vouchers:**
  - Header: Date\*, Type (Receipt, Payment, Contra, Journal, FeeInvoice)\*, Description\*.
  - Lines: Account, Amount, Debit or Credit.
  - List columns: Date, Voucher No, Type, Description, Amount, Status, Action. A permission covers reversing posted vouchers.
- **Fiscal Years:** Name\*, Start\*, End\*, Active, Closed.
- **Reports:** Trial Balance (date range; Code, Account Name, Main Category, Debit, Credit, Balance), Account Ledger (account and date range), Income Statement (Revenue, Expenses, Net Income), Balance Sheet (as of a date; Assets, Liabilities, Equity, Retained Earnings). Every report has Print.

## Registration kiosk (6 forms per branch)

1. **Trial Session / Starter Course Registration:** programs of interest, student details, parent or guardian, 31 "areas of interest" checkboxes, a 23-question personal interview, a liability and photo/video release, and customer and guardian signatures.
2. **Youth Martial Arts Enrollment Agreement:** 12-month minimum.
3. **Pre-K Martial Arts Enrollment Agreement:** 6-month minimum.
4. **Adult Martial Arts Enrollment Agreement:** 12-month minimum.
5. **Fighters Elite / MMA / Bushi Ban Combatives Enrollment Agreement:** 12-month minimum.
6. **Account / Agreement Change Form:** change or cancellation request, with a signature.

Forms 2 to 5 share one template. Only the program name and minimum term differ. Each one has:

- Student, co-signer and contact details.
- Tuition terms: commencement date, tuition amount, down payment, monthly installment, first and final payment dates, and payment method (EFT, Card, or Provided at front desk).
- The full enrollment agreement wording.
- Three acknowledgements and three signatures (student/parent, payee, receipt acknowledgement).

Card and bank details are never entered on the kiosk. The full wording of every form is in `Data/Seed/KioskForms.cs`.

The CRM's **Form Builder** manages these forms: categories (Admission, Agreement, ChangeForm, Generic), drafts and published versions, preview, "active on kiosk", and form order. Submissions keep the exact version that was signed.

## Other

- **Changelog:** release notes, managed in-app.
- **Settings and Help:** **Placeholder.**
- **Behaviour:** deleted records are hidden rather than erased, every dropdown is searchable, and a breadcrumb trail shows where you are.
- **Dashboard:** Active Students, Active Programs, Total Income and Total Expense, each over the last 30 days.
