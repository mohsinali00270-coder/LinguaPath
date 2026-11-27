import http from 'k6/http';
import { check } from 'k6';

export const BASE = __ENV.BASE_URL || 'http://localhost:5080';
export const PASSWORD = __ENV.PASSWORD || 'LoadTest123';
export const emailFor = (i) => `load-${i}@loadtest.linguapath.local`;

export function json(body) {
  return { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) };
}

export function login(i) {
  const res = http.post(`${BASE}/api/v1/auth/login`,
    JSON.stringify({ email: emailFor(i), password: PASSWORD, device: { deviceId: `k6-${i}`, platform: 'Android', appVersion: '1.0.0' } }),
    { headers: { 'Content-Type': 'application/json' }, tags: { name: 'login' } });
  check(res, { 'login 200': (r) => r.status === 200 });
  return res.status === 200 ? res.json('data.accessToken') : null;
}

export function auth(token) {
  return { headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' } };
}
