// Learner mix: Home (today + daily quiz), roadmap, a lecture, inbox, points. NFR-01: p95 < 500 ms.
import http from 'k6/http';
import { check, sleep, group } from 'k6';
import { BASE, login, auth } from './common.js';

const USERS = Number(__ENV.USERS || 100);
export const options = {
  scenarios: {
    learners: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '1m', target: USERS },
        { duration: '5m', target: USERS },
        { duration: '30s', target: 0 }
      ]
    }
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    'http_req_duration{name:today}': ['p(95)<500'],
    'http_req_duration{name:roadmap}': ['p(95)<500'],
    'http_req_duration{name:lecture}': ['p(95)<700'],
    'http_req_duration{name:inbox}': ['p(95)<300'],
    http_req_duration: ['p(95)<500']
  }
};

const tokens = {};

export default function () {
  const vu = __VU - 1;
  tokens[vu] = tokens[vu] || login(vu % USERS);
  const token = tokens[vu];
  if (!token) { sleep(5); return; }
  const a = auth(token);

  group('home', () => {
    const courses = http.get(`${BASE}/api/v1/learner-courses`, Object.assign({ tags: { name: 'courses' } }, a));
    check(courses, { 'courses 200': (r) => r.status === 200 });
    const list = courses.status === 200 ? courses.json('data') : [];
    if (!list || list.length === 0) return;
    const id = list[0].id;
    check(http.get(`${BASE}/api/v1/learner-courses/${id}/today`, Object.assign({ tags: { name: 'today' } }, a)),
      { 'today 200': (r) => r.status === 200 });
    const roadmap = http.get(`${BASE}/api/v1/learner-courses/${id}/roadmap`, Object.assign({ tags: { name: 'roadmap' } }, a));
    check(roadmap, { 'roadmap 200': (r) => r.status === 200 });
    const lectureId = roadmap.status === 200 ? roadmap.json('data.modules.0.lectures.0.lectureId') : null;
    if (lectureId) {
      check(http.get(`${BASE}/api/v1/lectures/${lectureId}?learnerCourseId=${id}`, Object.assign({ tags: { name: 'lecture' } }, a)),
        { 'lecture 200': (r) => r.status === 200 });
    }
  });

  check(http.get(`${BASE}/api/v1/notifications/unread-count`, Object.assign({ tags: { name: 'inbox' } }, a)),
    { 'inbox 200': (r) => r.status === 200 });
  check(http.get(`${BASE}/api/v1/points`, Object.assign({ tags: { name: 'points' } }, a)),
    { 'points ok': (r) => r.status === 200 || r.status === 404 });

  sleep(2 + Math.random() * 3); // think time
}
