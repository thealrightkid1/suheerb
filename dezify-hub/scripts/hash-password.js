'use strict';
// Usage: npm run hash-password -- "the password"
const { hashPassword } = require('../lib/sessions');
const pw = process.argv[2];
if (!pw) { console.error('Usage: npm run hash-password -- "your password"'); process.exit(1); }
console.log(hashPassword(pw));
