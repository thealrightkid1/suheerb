# Bushi Ban marketing website

The public front end. It's a single page: plain HTML, CSS and a little JavaScript. There's no build step and no framework, so it can be hosted anywhere (static hosting, IIS, Netlify, S3…).

```
index.html            the page
assets/css/site.css   all styling (colour rules are at the top)
assets/js/config.js   the ONLY file a developer needs to edit to connect the site
assets/js/site.js     menu, program filter, trial form, scroll effects
assets/img/           logos, icons and program photos
robots.txt, sitemap.xml
```

Preview it locally with `python3 -m http.server` in this folder, then open http://localhost:8000.

## For the developer: connecting it

Everything lives in `assets/js/config.js`:

| Setting | What it does |
| --- | --- |
| `loginUrl` | Every **Log in** button (header, members box, footer) goes here. Point it at the single login for CRM, attendance and registration. Default: `/login`. |
| `trialFormEndpoint` | The **Book a trial class** form POSTs JSON here. Until it's set, the form validates, shows the thank-you message and logs the data to the browser console. |
| `phone` | Phone number shown in the trial section and the footer. |

What the trial form sends:

```json
{
  "forWhom": "Child | Teen | Adult",
  "firstName": "", "lastName": "", "parentName": "",
  "phone": "", "email": "",
  "location": "Pearland",
  "program": "Youth Martial Arts",
  "message": "", "consent": "yes",
  "source": "Website",
  "submittedAt": "2026-11-17T16:00:00.000Z"
}
```

- `location` and `program` use the same names as the branches and programs in the CRM, so each submission can become a lead with source "Website".
- Return any 2xx status for success.
- If the endpoint is on another domain, allow CORS for the site's domain.

## Design

The design follows the Bushi Ban Dojo reference site: tall condensed uppercase headlines (Barlow Condensed), DM Sans for body text, square corners, hairline dividers and the red stamp.

Colour follows the **60-30-10 rule**:

| Share | Colour | Use |
| --- | --- | --- |
| 60% | Paper `#F6F3EE` (plus tints `#FFFDF8`, `#ECE7DF`) | Page background, light sections, cards |
| 30% | Ink `#151515` | Header, hero, dark sections, headlines, footer |
| 10% | Bushi Ban red `#C72E26` | Buttons, highlights, accent dots only |

Belt colours appear only in the belt-journey section, where they carry meaning.

## Before going live

- [ ] Confirm the domain and replace `https://www.bushiban.com/` in `index.html` (canonical and Open Graph tags), `robots.txt` and `sitemap.xml`.
- [ ] Add a street address for each school. Only Pasadena's (7770 Spencer Highway) was available, so the others show city and ZIP, and their Directions links run a map search.
- [ ] Check the program descriptions, especially Yo-Kung-Chi, Summer Camp and After School. They were written from program names and the reference site.
- [ ] Confirm the photos are cleared for marketing use. They come from the Bushi Ban Dojo reference site.
- [ ] Phone 281-479-7979 is from the reference site. Change `phone` in `config.js` if each school should show its own number.
- [ ] Decide whether the trial class is free. If it is, say so: "Book a free trial class" converts better.
