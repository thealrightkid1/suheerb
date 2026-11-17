/*
 * Site settings for the developer. This is the only file that needs editing to connect the site.
 *
 *  loginUrl           Where every "Log in" button goes (the one login for CRM, attendance and registration).
 *  trialFormEndpoint  URL that receives the trial-class form as JSON (POST). Leave empty until it's connected:
 *                     the form still shows a thank-you message, and logs the data to the browser console.
 *  phone / email      Shown in the footer and contact links when filled in.
 */
window.SITE_CONFIG = {
  loginUrl: "/login",
  trialFormEndpoint: "",
  phone: "281-479-7979",
  email: "",
};
