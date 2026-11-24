// Ponte de WhatsApp do iControl Monitor (Baileys, sessão por QR Code, como o WhatsApp Web).
// O serviço do Windows inicia este programa e conversa por linhas JSON:
//   entrada (stdin):  {"cmd":"send","id":"...","phone":"5521...","text":"..."} | {"cmd":"logout"} | {"cmd":"quit"}
//   saída (stdout):   {"ev":"status","status":"iniciando|aguardando_qr|conectado|desconectado|erro","phone":"","error":""}
//                     {"ev":"qr","qr":"..."} | {"ev":"sent","id":"..."} | {"ev":"failed","id":"...","error":"..."}
// A sessão fica na pasta passada em --auth (ProgramData do serviço). Só envia mensagens: não lê conversas.
const path = require("path");
const fs = require("fs");
const readline = require("readline");
const pino = require("pino");
const baileys = require("@whiskeysockets/baileys");

const makeWASocket = baileys.default || baileys.makeWASocket;
const { useMultiFileAuthState, DisconnectReason, fetchLatestBaileysVersion, Browsers } = baileys;

const argAuth = process.argv.indexOf("--auth");
const AUTH = argAuth > 0 ? process.argv[argAuth + 1] : path.join(process.cwd(), "whatsapp-auth");
const out = (obj) => process.stdout.write(JSON.stringify(obj) + "\n");
const logger = pino({ level: "silent" });

let sock = null;
let connected = false;
let stopping = false;
let retry = 0;

async function start() {
  out({ ev: "status", status: "iniciando" });
  fs.mkdirSync(AUTH, { recursive: true });
  const { state, saveCreds } = await useMultiFileAuthState(AUTH);
  let version;
  try { ({ version } = await fetchLatestBaileysVersion()); } catch { /* usa a versão embutida */ }
  sock = makeWASocket({
    auth: state, logger, version,
    browser: Browsers.windows("iControl Monitor"),
    markOnlineOnConnect: false, syncFullHistory: false, printQRInTerminal: false,
    shouldIgnoreJid: () => true, // não processa mensagens recebidas
  });
  sock.ev.on("creds.update", saveCreds);
  sock.ev.on("connection.update", (u) => {
    if (u.qr) { out({ ev: "qr", qr: u.qr }); out({ ev: "status", status: "aguardando_qr" }); }
    if (u.connection === "open") {
      connected = true; retry = 0;
      const phone = String(sock.user?.id || "").split(":")[0].split("@")[0];
      out({ ev: "status", status: "conectado", phone });
    }
    if (u.connection === "close") {
      connected = false;
      const code = u.lastDisconnect?.error?.output?.statusCode;
      const loggedOut = code === DisconnectReason.loggedOut;
      if (loggedOut) {
        try { fs.rmSync(AUTH, { recursive: true, force: true }); } catch { /* ignora */ }
        out({ ev: "status", status: "desconectado", error: "O número foi desconectado. Leia o QR Code de novo." });
      } else {
        out({ ev: "status", status: "desconectado", error: u.lastDisconnect?.error?.message || "" });
      }
      if (!stopping) setTimeout(() => start().catch(fail), Math.min(60000, 2000 * 2 ** Math.min(retry++, 5)));
    }
  });
}

function fail(e) { out({ ev: "status", status: "erro", error: String(e?.message || e) }); }

async function send({ id, phone, text }) {
  try {
    if (!connected || !sock) throw new Error("WhatsApp não conectado");
    const digits = String(phone || "").replace(/\D/g, "");
    const [info] = await sock.onWhatsApp(digits);
    if (!info?.exists) throw new Error("Este número não tem WhatsApp");
    await sock.sendMessage(info.jid, { text: String(text || "") });
    out({ ev: "sent", id });
  } catch (e) {
    out({ ev: "failed", id, error: String(e?.message || e) });
  }
}

const rl = readline.createInterface({ input: process.stdin });
rl.on("line", async (line) => {
  let m; try { m = JSON.parse(line); } catch { return; }
  if (m.cmd === "send") await send(m);
  else if (m.cmd === "logout") { try { await sock?.logout(); } catch { /* já saiu */ } }
  else if (m.cmd === "quit") { stopping = true; try { sock?.end(undefined); } catch { /* ignora */ } process.exit(0); }
});
rl.on("close", () => process.exit(0)); // o serviço fechou: encerra junto

start().catch(fail);
