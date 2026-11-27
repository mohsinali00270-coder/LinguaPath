// Registers USERS learners on a staging API. They still need their email confirmed: on the staging database run
//   UPDATE AspNetUsers SET EmailConfirmed = 1 WHERE Email LIKE 'load-%@loadtest.linguapath.local';
// and give each a course in the app (or onboard one and copy it) before running api-smoke.js.
import http from 'k6/http';
import { check } from 'k6';
import { BASE, PASSWORD, emailFor } from './common.js';

const USERS = Number(__ENV.USERS || 100);
export const options = { scenarios: { seed: { executor: 'shared-iterations', vus: 10, iterations: USERS } } };

export default function () {
  const i = __ITER;
  const res = http.post(`${BASE}/api/v1/auth/register`, JSON.stringify({
    fullName: `Load Learner ${i}`, email: emailFor(i), password: PASSWORD, timeZone: 'UTC', uiLanguage: 'en'
  }), { headers: { 'Content-Type': 'application/json' } });
  check(res, { 'registered or exists': (r) => r.status === 200 || r.status === 409 });
}
