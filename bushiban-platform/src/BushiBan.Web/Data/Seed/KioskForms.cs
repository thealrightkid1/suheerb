// The registration kiosk forms from the original system, with their full wording.
// Every branch gets its own copy, which staff can then edit in the Form Builder.
using BushiBan.Web.Domain;
using BushiBan.Web.Services.Forms;

namespace BushiBan.Web.Data.Seed;

public static class KioskForms
{
    public record Seed(string Name, FormCategory Category, string? ProgramName, List<FormField> Fields);

    public static IEnumerable<Seed> All()
    {
        yield return new("Trial Session / Starter Course Registration", FormCategory.Admission, null, TrialForm());
        yield return new("Youth Martial Arts Enrollment Agreement", FormCategory.Agreement, "Youth Martial Arts", Agreement("Youth Martial Arts Program", 12));
        yield return new("Pre-K Martial Arts Enrollment Agreement", FormCategory.Agreement, "Pre-K Martial Arts", Agreement("Pre-K Martial Arts Program", 6));
        yield return new("Adult Martial Arts Enrollment Agreement", FormCategory.Agreement, "Adult Martial Arts", Agreement("Adult Martial Arts Program", 12));
        yield return new("Fighters Elite / MMA / Bushi Ban Combatives Enrollment Agreement", FormCategory.Agreement, "MMA", Agreement("Fighters Elite Program", 12));
        yield return new("Account / Agreement Change Form", FormCategory.ChangeForm, null, ChangeForm());
    }

    private static FormField F(string key, string type, string label, bool required = false, string width = "half",
        string? mapsTo = null, IEnumerable<string>? options = null, string? help = null) =>
        new() { Key = key, Type = type, Label = label, Required = required, Width = width, MapsTo = mapsTo, Options = options?.ToList(), Help = help };

    private static FormField H(string label) => new() { Key = "h_" + Guid.NewGuid().ToString("N")[..8], Type = "heading", Label = label, Width = "full" };
    private static FormField Notice(string html) => new() { Key = "n_" + Guid.NewGuid().ToString("N")[..8], Type = "notice", Html = html, Width = "full" };
    private static readonly string[] YesNo = { "Yes", "No" };

    private static IEnumerable<FormField> StudentBlock(bool trial)
    {
        yield return F("studentFirstName", "text", "Student First Name", true, mapsTo: "FirstName");
        yield return F("studentLastName", "text", "Student Last Name", true, mapsTo: "LastName");
        if (trial)
        {
            yield return F("formDate", "date", "Today's Date");
            yield return F("parentName", "text", "Parent / Guardian Name", true, mapsTo: "FatherName");
        }
        yield return F("dob", "date", "Date of Birth", true, mapsTo: "DateOfBirth");
        yield return F("sex", "select", "Sex", mapsTo: "Gender", options: new[] { "Male", "Female", "Other" });
        yield return F("age", "number", trial ? "Age (Yrs)" : "Age");
        yield return F("emergencyContactName", "text", "Emergency Contact Name", true);
        yield return F("emergencyContactPhone", "tel", "Emergency Contact Phone", true);
        yield return F("address", "text", "Address", mapsTo: "Address1");
        yield return F("city", "city", "City", mapsTo: "CityId");
        yield return F("zip", "text", "Zip", mapsTo: "ZipCode");
    }

    private static List<FormField> TrialForm()
    {
        var f = new List<FormField>
        {
            H("Registration"),
            Notice("<p>In order to cater to your needs in a more direct and personal manner, and to help serve and educate you to the best of our ability, please complete the following.</p>"),
            F("programs", "checkboxes", "Program(s) of interest", width: "full", options: new[] {
                "Black Belt Program – Traditional Bushi Ban Martial Arts", "After School Program", "Pre-K (Little Warriors)",
                "Mixed Martial Arts (MMA)", "Power Kickboxing/Fitness", "Jiu Jitsu/Grappling", "Other" }),
            F("otherProgram", "text", "Other (specify)", width: "full"),
        };
        f.AddRange(StudentBlock(trial: true));
        f.AddRange(new[]
        {
            F("email", "email", "Email", mapsTo: "Email1"),
            F("occupation", "text", "Occupation"),
            F("cellPhone", "tel", "Cell Phone", mapsTo: "Phone1"),
            F("altPhone", "tel", "Additional Phone", mapsTo: "Phone2"),
            F("referredBy", "text", "Who sponsored/referred you to Bushi Ban?", width: "full", mapsTo: "MarketingSourceDetail"),
            Notice("<p>Please check all of the following that apply and/or notate which are the most important areas to you:</p>"),
            F("motivation", "checkboxes", "Areas of Interest", width: "full", options: new[] {
                "Improve Focus", "Learning New Skills", "Hobby/Recreation", "Improved Grades", "Anti-Bullying", "Temper Control",
                "Conflict Resolution", "Performance Enhancement", "Respect", "Sporting Activity", "New Activity", "Lifestyle Change",
                "Study as an Art Form", "Athletic Skill", "Combative Sport", "Interested in Competition", "Self-Control",
                "Self-Confidence", "Self-Defense", "Self-Discipline", "Self-Improvement", "External Influence", "Notable Life Event",
                "Other", "Discipline", "Increased Energy", "Improved Fitness", "Tone/Sculpting", "Weight Loss", "Stress Management",
                "Energy Release" }),
            Notice("<p>Many individuals begin their journey in Martial Arts for a number of reasons. If there is anything else or other reason(s) that you believe is important that has sparked your interest in the Martial Arts not listed above, please provide extra comments or elaborate below.</p>"),
            F("comments", "textarea", "Additional Comments", width: "full", mapsTo: "AdditionalInformation"),

            H("Personal Interview"),
            F("q1Motivation", "text", "What motivated you to come in today?", width: "full"),
            F("q2HearAbout", "text", "How did you hear about our school?", width: "full", mapsTo: "MarketingSource"),
            F("q3TrainedBefore", "radio", "Have you ever trained in the Martial Arts before?", options: YesNo),
            F("q3TrainedDetail", "text", "If yes, where and how long?"),
            F("q4ExerciseRegular", "radio", "Is exercise a regular part of your lifestyle?", options: YesNo),
            F("q4ExerciseType", "text", "If yes, what type?"),
            F("q5OtherSports", "text", "Do you or your child participate in any other sports?", width: "full"),
            F("q6ClassTime", "radio", "Are you looking for morning or evening classes?", options: new[] { "Morning", "Evening" }),
            F("q7LessonType", "radio", "Are you interested in group or private lessons?", options: new[] { "Group", "Private", "Both" }),
            F("q8TimesPerWeek", "number", "How many times per week would you like to train?"),
            F("q9LiveInArea", "radio", "Will you be living in the area for at least one year?", options: YesNo),
            F("q10GoodHealth", "radio", "Are you/your child in good physical health?", options: YesNo),
            F("q10HealthExplain", "text", "If no, explain", mapsTo: "MedicalConcerns"),
            F("q11SetGoal", "radio", "Are you willing to set a goal to develop your mental discipline and physical fitness?", options: YesNo),
            F("q12EstablishedTime", "radio", "Have you established a personal time to achieve this goal?", options: YesNo),
            F("q12TimeExplain", "text", "If yes, explain"),
            F("q13Recommend", "radio", "When satisfied with our program, will you recommend Bushi Ban to others?", options: YesNo),
            F("q14AttendTwice", "radio", "Are you able to attend class regularly at least twice per week?", options: YesNo),
            F("q15aReadCreed", "radio", "Have you read the Student Creed?", options: YesNo),
            F("q15bAgreeCreed", "radio", "Do you agree with our Student Creed?", options: YesNo),
            F("q16BlackBeltMeaning", "textarea", "What does a Black Belt mean to you?", width: "full"),
            F("q17PracticeDaily", "radio", "Can you commit at least 15 minutes a day to practice at home?", options: YesNo),
            Notice("<p>There are three types of people in the world: 1. Those who make things happen, 2. Those who watch things happen, and 3. Those who don't even know what's happening.</p>"),
            F("q18PersonType", "radio", "Which type of person would you like to be?", options: new[] { "1", "2", "3" }),
            F("q19BestQualities", "text", "In your opinion, what are your/your child's two best qualities?", width: "full"),
            F("q20ShareQualities", "radio", "Are you willing to share your qualities with others?", options: YesNo),
            F("q21OptimistPessimist", "radio", "If people were divided into categories — optimist & pessimist — which would you be?", options: new[] { "Optimist", "Pessimist" }),
            F("q22ApplyFully", "radio", "Will you apply yourself fully to develop knowledge, honesty, and strength, which are the central themes of Martial Arts Philosophy?", width: "full", options: YesNo),
            F("q23MentallyPrepared", "radio", "If accepted into our program, are you mentally prepared to follow the rules and philosophy of Martial Arts discipline?", width: "full", options: YesNo),

            H("Liability & Photo/Video Release"),
            Notice("<p><strong>MARTIAL ARTS IS A STRENUOUS ACTIVITY WHICH COULD RESULT IN SIGNIFICANT PHYSICAL INJURY/INJURIES</strong></p>" +
                   "<p>The Customer understands that the starter course may consist of strenuous physical exercise and movements and that he/she may suffer personal injury. The customer agrees to forever relieve and hold harmless Bushi Ban International, its employees, instructors and other customers from any and all liability or damages resulting from either personal injury, theft or loss of personal property whether or not due to negligence of Bushi Ban International, its employees, instructors, other customers or equipment. I understand the above paragraph completely and clearly and being of sound mind and body willingly sign this release of liability. I understand while participating in this activity, I may be photographed. I agree to allow my photo, video, or film likeness to be used for any legitimate advertising, marketing or PR purpose.</p>"),
            F("releaseAck", "checkbox", "I have read and agree to the release of liability and photo/video release above", true, "full"),
            F("customerSignature", "signature", "Customer Signature", true),
            F("signatureDate", "date", "Date", true),
            F("guardianSignature", "signature", "Parent/Guardian Signature"),
            F("guardianDate", "date", "Parent/Guardian Date"),
        });
        return f;
    }

    private static List<FormField> Agreement(string programName, int months)
    {
        var f = new List<FormField>
        {
            Notice($"<p>Minimum term of enrollment: <strong>{months} months</strong>, or as approved by the <strong>Enrollment director</strong>.</p>"),
        };
        f.AddRange(StudentBlock(trial: false));
        f.AddRange(new[]
        {
            F("coSignerName", "text", "Co-Signer / Responsible Party Name", true, mapsTo: "FatherName"),
            F("coSignerFirstName", "text", "Co-Signer First Name"),
            F("coSignerLastName", "text", "Co-Signer Last Name"),
            F("coSignerDob", "date", "Co-Signer Date of Birth"),
            F("cellPhone", "tel", "Cell Phone", true, mapsTo: "Phone1"),
            F("homePhone", "tel", "Home Phone", mapsTo: "Phone2"),
            F("workPhone", "tel", "Work Phone"),
            F("email", "email", "Email", mapsTo: "Email1"),
            F("commencementDate", "date", "Commencement Date"),
            F("tuitionChangeDate", "date", "Tuition Charge Date"),
            F("tuitionChangeAmount", "number", "Tuition Amount"),
            F("downPayment", "number", "Down Payment"),
            F("monthlyInstallment", "number", "Monthly Installment Amount"),
            F("firstPaymentDate", "date", "First Payment Date"),
            F("finalPaymentDate", "date", "Final Payment Date"),
            Notice("<p><em>Card or bank-draft details are collected securely at the front desk and are not entered on this kiosk.</em></p>"),
            F("paymentMethod", "select", "Payment Method", options: new[] { "EFT", "Card", "Provided at front desk" }),
            F("bankName", "text", "Bank Name"),
            F("cardBrand", "select", "Card Brand", options: new[] { "Visa", "MasterCard", "Discover", "Amex" }),
            Notice(AgreementText(programName, months)),
            F("acknowledgedClauses", "checkboxes", "I acknowledge the following", true, "full",
                options: new[] { "Liability waiver", "Payment terms", "Cancellation policy" }),
            F("studentParentSignature", "signature", "Student / Parent Signature", true),
            F("payeeSignature", "signature", "Payee Signature"),
            F("receiptAckSignature", "signature", "Receipt Acknowledgement Signature", true),
        });
        return f;
    }

    private static List<FormField> ChangeForm() => new()
    {
        Notice("<p>Changes and cancellations require 30 days written notice and are processed strictly upon completion of the contractual obligation only.</p>"),
        F("studentFirstName", "text", "Student First Name", true, mapsTo: "FirstName"),
        F("studentLastName", "text", "Student Last Name", true, mapsTo: "LastName"),
        F("printName", "text", "Print Name", true),
        F("requestKind", "select", "Request", true, options: new[] { "Change", "Cancellation" }),
        F("accountNumber", "text", "Account Number"),
        F("accountSystem", "select", "Account System", options: new[] { "EFC", "In-House" }),
        F("obligationCompletionDate", "date", "Contract Obligation Completion Date"),
        F("startDate", "date", "Start Date"),
        F("lastClassDate", "date", "Last Class Date"),
        F("lastPaymentDate", "date", "Last Payment Date"),
        F("age", "number", "Age"),
        F("rank", "text", "Rank"),
        F("program", "text", "Program"),
        F("instructor", "text", "Instructor"),
        F("explanation", "textarea", "Please Explain", true, "full"),
        F("signature", "signature", "Signature", true),
    };

    private static string AgreementText(string programName, int months) => $@"
<h5>BUSHI BAN INTERNATIONAL, LLC — STUDENT ENROLLMENT AGREEMENT</h5>
<p>I UNDERSTAND that under the terms of this agreement, BUSHI BAN INTERNATIONAL, LLC obligates itself to furnish me with the competent instruction and suitable facilities for teaching lessons. All class sessions are supervised by qualified personnel trained in the procedures and traditions of the martial arts. The regular hourly tuition is $35.00 per hour; however, a substantial discount has been provided to me by virtue of enrolling in a regular course.</p>
<p>THE STUDENT hereby represents that he/she is physically fit to receive and participate in the prescribed course of instruction.</p>
<p>I UNDERSTAND that my tuition is arranged to be made in monthly installments and is not affected by my lesson schedule and/or attendance.</p>
<p>I UNDERSTAND and agree that the school will not be held liable for injuries, damages, etc., not caused by or resulting from the negligence of the owners, operators, employees or persons in charge of such establishment.</p>
<p>I WILL faithfully comply with all the rules and regulations of the school and the traditions of the martial arts. I further understand that failure to complete the lessons does not relieve me of my obligations to pay the tuition in full.</p>
<p>THE UNDERSIGNED AGREES to receive and participate in and BUSHI BAN INTERNATIONAL, LLC agrees to teach a course of Martial Arts lessons consistent with the Bushi Ban Curriculum for the {programName} continuing for a minimum of {months} months.</p>
<p>THE UNDERSIGNED AGREES that this is an open agreement with BUSHI BAN INTERNATIONAL, LLC and can only be changed by means of written instructions delivered in person by the undersigned or delivered by certified mail from the undersigned. Early cancellation will result in a $250 cancellation fee or the remainder of the agreement whichever is less. All changes to this agreement will be submitted to BUSHI BAN INTERNATIONAL, LLC with a minimum of thirty (30) days to process, any payments due during that time will be collected. BUSHI BAN INTERNATIONAL, LLC maintains a strict no refund policy.</p>
<p>As REQUIRED by General Obligations Law, you have certain rights to cancel this agreement. These are set forth in full on the reverse side of this agreement and are made a part of this agreement. You may cancel this agreement without any penalty or further obligations within three (3) days from the date of this agreement. Notice of cancellation shall be in writing and mailed to BUSHI BAN INTERNATIONAL, LLC by registered or certified mail.</p>
<p>A LATE CHARGE of fifteen dollars ($15) will be assessed for any payments seven (7) days past due.</p>
<p>All tuition will be paid directly to Educational Funding Company (EFC) / BUSHI BAN INTERNATIONAL (BBI). Payments presented through Automatic Clearing House (ACH) against accounts with insufficient funds will be re-presented in 15 days; in addition the student will be charged a $15 returned item fee. Credit card systems are subject to a $7.50 return item fee. TUITION PAYMENTS MADE WITH CREDIT OR DEBIT CARDS WILL INCUR A 2% SERVICE FEE. Federal Law gives you certain rights to stop an automatic charge against your credit card or bank account under this contract.</p>
<p>SPECIAL INSTRUCTIONS: THERE WILL BE NO HOLDS OR FREEZES ON THIS ACCOUNT PRIOR TO COMPLETION OF CONTRACT COMMITMENT. CANCELLATION AFTER COMPLETION OF THE {months}-MONTH COMMITMENT REQUIRES WRITTEN NOTIFICATION AND CAN TAKE UP TO THIRTY DAYS TO PROCESS. ALL PAYMENTS DURING THE PROCESSING TIME WILL BE COLLECTED.</p>
<p>CONSUMER'S RIGHT TO CANCELLATION. You may cancel this contract without any penalty or further obligation within three (3) days from the date of this agreement and receive full refund on down payment. Notice of cancellation shall be in writing and delivered in person or by certified mail to BUSHI BAN INTERNATIONAL, LLC. If you move your residence more than 25 miles from BUSHI BAN INTERNATIONAL, LLC, located at 7770 Spencer Highway, Pasadena, Texas, this agreement can be transferred to any BUSHI BAN INTERNATIONAL, LLC affiliate or Educational Funding Company School. In the case where a school cannot be provided, cancellation under this section requires written proof of new permanent address, phone number, name and address of new employer and requires 30-day advance written notice. A residence investigation will then be conducted.</p>
<h6>NOTICE OF CONSUMER'S RIGHTS.</h6>
<ul>
<li>All electronic fund transfer and credit card payments will be made to the Educational Funding Company; their contact number is (301) 654-8677, our school registration number is 712. Any contact with the Educational Funding Company should make reference to this number.</li>
<li>Our school is not required to carry a performance bond under regulations since we do not collect more than three months tuition in advance or an initiation fee of over $200.</li>
<li>You have the right to cancel this contract within three (3) days after receipt of a copy of this contract. Cancellations must be in writing, and delivered either in person or by certified or registered mail to BUSHI BAN INTERNATIONAL, LLC. You are entitled to a full refund if cancellation is received within three (3) days.</li>
<li>If you become disabled for at least three (3) months during the membership terms and that disability is confirmed in writing by a physician, you have a right to an extension and/or termination of the contract.</li>
<li>If the school is closed for a month or more, you are entitled to your choice of either an extension of the contract or prorated refund, except if the closing is not the fault of the facility, in which case the choice of remedy is the school's.</li>
<li>This Notice of Consumer's Rights is an integral part of the Application and Contract for Membership.</li>
</ul>";
}
