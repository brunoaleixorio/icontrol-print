// Ponte de WhatsApp do iControl Monitor (Baileys, sessão por QR Code, como o WhatsApp Web).
// O serviço do Windows inicia este programa e conversa por linhas JSON:
//   entrada (stdin):  {"cmd":"send","id":"...","phone":"5521...","text":"..."} | {"cmd":"logout"} | {"cmd":"quit"}
//   saída (stdout):   {"ev":"status","status":"iniciando|aguardando_qr|conectado|desconectado|erro","phone":"","error":""}
//                     {"ev":"qr","qr":"..."} | {"ev":"sent","id":"..."} | {"ev":"failed","id":"...","error":"..."}
//                     {"ev":"inbound","from":"5521...","name":"...","text":"...","type":"text|image|audio...","from_me":false}
// Mensagens recebidas (só conversas individuais: nada de grupos, status ou canais) vão para o robô de atendimento no banco.
// A sessão fica na pasta passada em --auth (ProgramData do serviço).
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
const sentIds = new Set();      // mensagens enviadas por este programa (não contam como "respondido pelo celular")
const lidJids = new Map();      // contato que só aparece por LID: dígitos -> jid, para conseguir responder
const startedAt = Math.floor(Date.now() / 1000);

const isPersonal = (jid = "") => jid.endsWith("@s.whatsapp.net") || jid.endsWith("@lid");
const ignoreJid = (jid = "") => !isPersonal(jid); // grupos, status, canais, listas de transmissão

function textOf(m = {}) {
  const msg = m.ephemeralMessage?.message || m.viewOnceMessage?.message || m.viewOnceMessageV2?.message || m;
  if (msg.conversation) return { type: "text", text: msg.conversation };
  if (msg.extendedTextMessage) return { type: "text", text: msg.extendedTextMessage.text || "" };
  if (msg.buttonsResponseMessage) return { type: "text", text: msg.buttonsResponseMessage.selectedDisplayText || "" };
  if (msg.listResponseMessage) return { type: "text", text: msg.listResponseMessage.title || "" };
  if (msg.imageMessage) return { type: "image", text: msg.imageMessage.caption || "" };
  if (msg.videoMessage) return { type: "video", text: msg.videoMessage.caption || "" };
  if (msg.audioMessage) return { type: "audio", text: "" };
  if (msg.documentMessage) return { type: "document", text: msg.documentMessage.fileName || "" };
  if (msg.stickerMessage) return { type: "sticker", text: "" };
  if (msg.locationMessage) return { type: "location", text: "" };
  return null; // reações, confirmações de leitura, mensagens apagadas etc.
}

function phoneOf(key = {}) {
  for (const j of [key.senderPn, key.remoteJidAlt, key.participantPn, key.remoteJid]) {
    if (typeof j === "string" && j.endsWith("@s.whatsapp.net")) return { phone: j.split("@")[0].split(":")[0], jid: j };
  }
  const lid = String(key.remoteJid || "");
  if (lid.endsWith("@lid")) { const d = lid.split("@")[0].split(":")[0]; lidJids.set(d, lid); return { phone: d, jid: lid }; }
  return null;
}

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
    shouldIgnoreJid: ignoreJid,
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
      if (!stopping) setTimeout(() => start().catch(fail), loggedOut ? 1500 : Math.min(60000, 2000 * 2 ** Math.min(retry++, 5)));
    }
  });
  // mensagens novas (type "notify"); o histórico antigo não é repassado
  sock.ev.on("messages.upsert", ({ messages, type }) => {
    if (type !== "notify") return;
    for (const m of messages || []) {
      try {
        const jid = m.key?.remoteJid || "";
        if (!isPersonal(jid)) continue;
        if (Number(m.messageTimestamp || 0) < startedAt - 60) continue;
        if (m.key.fromMe && sentIds.has(m.key.id)) continue; // enviada pelo próprio Monitor
        const body = textOf(m.message || {});
        if (!body) continue;
        const who = phoneOf(m.key);
        if (!who) continue;
        out({ ev: "inbound", from: who.phone, name: m.key.fromMe ? "" : (m.pushName || ""), text: body.text, type: body.type, from_me: !!m.key.fromMe });
      } catch { /* ignora mensagem que não deu para ler */ }
    }
  });
}

function fail(e) { out({ ev: "status", status: "erro", error: String(e?.message || e) }); }

async function send({ id, phone, text }) {
  try {
    if (!connected || !sock) throw new Error("WhatsApp não conectado");
    const digits = String(phone || "").replace(/\D/g, "");
    let jid = lidJids.get(digits);
    if (!jid) {
      const [info] = await sock.onWhatsApp(digits);
      if (!info?.exists) throw new Error("Este número não tem WhatsApp");
      jid = info.jid;
    }
    const r = await sock.sendMessage(jid, { text: String(text || "") });
    if (r?.key?.id) { sentIds.add(r.key.id); if (sentIds.size > 2000) sentIds.delete(sentIds.values().next().value); }
    out({ ev: "sent", id });
  } catch (e) {
    out({ ev: "failed", id, error: String(e?.message || e) });
  }
}

const rl = readline.createInterface({ input: process.stdin });
rl.on("line", async (line) => {
  let m; try { m = JSON.parse(line); } catch { return; }
  if (m.cmd === "send") await send(m);
  else if (m.cmd === "logout") {
    // desconecta o número; o "close" com loggedOut apaga a sessão e mostra um QR Code novo
    try { if (connected) await sock?.logout(); else { try { sock?.end(undefined); } catch { /* ok */ } fs.rmSync(AUTH, { recursive: true, force: true }); } } catch { /* já saiu */ }
  }
  else if (m.cmd === "quit") { stopping = true; try { sock?.end(undefined); } catch { /* ignora */ } process.exit(0); }
});
rl.on("close", () => process.exit(0)); // o serviço fechou: encerra junto

start().catch(fail);
