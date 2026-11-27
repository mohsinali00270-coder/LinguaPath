// Chat round trip over SignalR (JSON protocol over WebSockets). NFR-02: message delivery < 1 s.
// Each VU joins the public load-test group (create one named "k6 load" in the app first, set GROUP_ID).
import ws from 'k6/ws';
import http from 'k6/http';
import { check } from 'k6';
import { Trend, Rate } from 'k6/metrics';
import { BASE, login, auth } from './common.js';

const USERS = Number(__ENV.USERS || 50);
const GROUP_ID = Number(__ENV.GROUP_ID || 0);
const roundTrip = new Trend('chat_round_trip_ms', true);
const delivered = new Rate('chat_delivered');
const RS = String.fromCharCode(0x1e); // SignalR record separator

export const options = {
  scenarios: { chatters: { executor: 'constant-vus', vus: USERS, duration: '3m' } },
  thresholds: { chat_round_trip_ms: ['p(95)<1000'], chat_delivered: ['rate>0.99'] }
};

export default function () {
  const token = login((__VU - 1) % USERS);
  if (!token || !GROUP_ID) return;
  http.post(`${BASE}/api/v1/conversations/${GROUP_ID}/join`, null, auth(token));

  const url = `${BASE.replace('http', 'ws')}/hubs/chat?access_token=${token}`;
  const pending = {};
  const res = ws.connect(url, null, (socket) => {
    socket.on('open', () => {
      socket.send(JSON.stringify({ protocol: 'json', version: 1 }) + RS);
      socket.setInterval(() => {
        const tag = `k6-${__VU}-${Date.now()}`;
        pending[tag] = Date.now();
        http.post(`${BASE}/api/v1/conversations/${GROUP_ID}/messages`, JSON.stringify({ text: tag, replyToMessageId: null, clientId: tag }), auth(token));
      }, 5000);
    });
    socket.on('message', (raw) => {
      raw.split(RS).filter((m) => m).forEach((m) => {
        const msg = JSON.parse(m);
        if (msg.type !== 1) return; // invocations only
        const body = msg.arguments && msg.arguments[0] && (msg.arguments[0].text || msg.arguments[0].body);
        if (body && pending[body]) {
          roundTrip.add(Date.now() - pending[body]);
          delivered.add(true);
          delete pending[body];
        }
      });
    });
    socket.setTimeout(() => {
      Object.keys(pending).forEach(() => delivered.add(false));
      socket.close();
    }, 60000);
  });
  check(res, { 'ws 101': (r) => r && r.status === 101 });
}
