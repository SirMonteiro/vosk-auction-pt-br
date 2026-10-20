/**
 * Push-to-talk client.
 *
 * Protocol (see docs/unity-handoff.md):
 *   -> {"type":"start"}
 *   -> binary 16 kHz mono s16le PCM, 100 ms frames
 *   -> {"type":"stop"}
 *   <- {"type":"ready"} | {"type":"partial"} | {"type":"final"} | {"type":"error"}
 *
 * Two details are not optional:
 *  - a 250 ms pre-roll buffer, because Vosk drops the first syllable otherwise;
 *  - 400 ms of trailing silence before `stop`, measured to improve accuracy.
 */
(() => {
  "use strict";

  const SAMPLE_RATE = 16000;
  const FRAME_MS = 100;
  const FRAME_SAMPLES = (SAMPLE_RATE * FRAME_MS) / 1000;
  const PRE_ROLL_MS = 250;
  const PRE_ROLL_SAMPLES = (SAMPLE_RATE * PRE_ROLL_MS) / 1000;
  const TAIL_SILENCE_MS = 400;

  const el = {
    ptt: document.getElementById("ptt"),
    pttTitle: document.getElementById("pttTitle"),
    pttTimer: document.getElementById("pttTimer"),
    partial: document.getElementById("partialText"),
    value: document.getElementById("resultValue"),
    kind: document.getElementById("resultKind"),
    mode: document.getElementById("resultMode"),
    latency: document.getElementById("resultLatency"),
    numbers: document.getElementById("resultNumbers"),
    flagAdd: document.getElementById("flagAdd"),
    flagSum: document.getElementById("flagSum"),
    flagMult: document.getElementById("flagMult"),
    nbestPanel: document.getElementById("nbestPanel"),
    nbestList: document.getElementById("nbestList"),
    log: document.getElementById("log"),
    copyBtn: document.getElementById("copyBtn"),
    clearBtn: document.getElementById("clearBtn"),
    appendToggle: document.getElementById("appendToggle"),
    charCount: document.getElementById("charCount"),
    status: document.getElementById("statusText"),
  };

  const state = {
    socket: null,
    context: null,
    stream: null,
    source: null,
    worklet: null,
    recording: false,
    spaceDown: false,
    startedAt: 0,
    timer: null,
    pending: [],
    preRoll: [],
    preRollSamples: 0,
    lastResult: null,
    history: [],
  };

  // ------------------------------------------------------------------
  // microphone
  // ------------------------------------------------------------------
  async function ensureMicrophone() {
    if (state.stream) return true;
    try {
      state.stream = await navigator.mediaDevices.getUserMedia({
        audio: {
          channelCount: 1,
          echoCancellation: false,
          noiseSuppression: false,
          autoGainControl: false,
        },
      });
      state.context = new AudioContext({ sampleRate: SAMPLE_RATE });
      state.source = state.context.createMediaStreamSource(state.stream);
      await attachProcessor();
      setStatus("microfone pronto");
      return true;
    } catch (err) {
      setStatus("microfone indisponível: " + err.message);
      el.pttTitle.textContent = "Microfone bloqueado — verifique a permissão";
      return false;
    }
  }

  async function attachProcessor() {
    // AudioWorklet is preferred; ScriptProcessor is the fallback.
    if (state.context.audioWorklet) {
      try {
        const code = `
          class Capture extends AudioWorkletProcessor {
            process(inputs) {
              const channel = inputs[0][0];
              if (channel) this.port.postMessage(channel.slice(0));
              return true;
            }
          }
          registerProcessor("capture", Capture);
        `;
        const url = URL.createObjectURL(new Blob([code], { type: "application/javascript" }));
        await state.context.audioWorklet.addModule(url);
        URL.revokeObjectURL(url);
        state.worklet = new AudioWorkletNode(state.context, "capture");
        state.worklet.port.onmessage = (event) => onAudio(event.data);
        state.source.connect(state.worklet);
        // Worklets need a sink to be pulled; a muted gain node is enough.
        const mute = state.context.createGain();
        mute.gain.value = 0;
        state.worklet.connect(mute).connect(state.context.destination);
        return;
      } catch (err) {
        console.warn("AudioWorklet failed, falling back to ScriptProcessor", err);
      }
    }

    const processor = state.context.createScriptProcessor(4096, 1, 1);
    processor.onaudioprocess = (event) => onAudio(event.inputBuffer.getChannelData(0).slice(0));
    state.source.connect(processor);
    processor.connect(state.context.destination);
    state.worklet = processor;
  }

  // ------------------------------------------------------------------
  // audio handling
  // ------------------------------------------------------------------
  function onAudio(float32) {
    if (state.recording) {
      state.pending.push(float32);
      return;
    }
    // Idle: keep only the last PRE_ROLL_SAMPLES for first-syllable protection.
    state.preRoll.push(float32);
    state.preRollSamples += float32.length;
    while (state.preRollSamples > PRE_ROLL_SAMPLES && state.preRoll.length > 1) {
      state.preRollSamples -= state.preRoll.shift().length;
    }
  }

  function floatToInt16(float32) {
    const out = new Int16Array(float32.length);
    for (let i = 0; i < float32.length; i += 1) {
      const sample = Math.max(-1, Math.min(1, float32[i]));
      out[i] = sample < 0 ? sample * 0x8000 : sample * 0x7fff;
    }
    return out;
  }

  function sendAudio(float32) {
    if (!state.socket || state.socket.readyState !== WebSocket.OPEN) return;
    const pcm = floatToInt16(float32);
    state.socket.send(pcm.buffer);
  }

  function flushFrames(chunks, { padToFrame = true } = {}) {
    if (!chunks.length) return;

    const total = chunks.reduce((sum, chunk) => sum + chunk.length, 0);
    let merged = new Float32Array(total);
    let offset = 0;
    for (const chunk of chunks) {
      merged.set(chunk, offset);
      offset += chunk.length;
    }

    if (padToFrame && merged.length % FRAME_SAMPLES !== 0) {
      const padded = new Float32Array(
        Math.ceil(merged.length / FRAME_SAMPLES) * FRAME_SAMPLES,
      );
      padded.set(merged);
      merged = padded;
    }
    sendAudio(merged);
  }

  // ------------------------------------------------------------------
  // websocket
  // ------------------------------------------------------------------
  function connect() {
    if (state.socket && state.socket.readyState <= WebSocket.OPEN) return state.socket;

    const scheme = window.location.protocol === "https:" ? "wss" : "ws";
    const socket = new WebSocket(`${scheme}://${window.location.host}/stream`);
    socket.binaryType = "arraybuffer";

    socket.onmessage = (event) => handleMessage(JSON.parse(event.data));
    socket.onclose = () => {
      state.socket = null;
      if (state.recording) stopRecording();
    };
    socket.onerror = () => setStatus("erro no websocket");

    state.socket = socket;
    return socket;
  }

  function handleMessage(message) {
    switch (message.type) {
      case "ready":
        setStatus("gravando");
        break;
      case "partial":
        el.partial.textContent = message.text;
        break;
      case "final":
        renderResult(message);
        break;
      case "error":
        setStatus("erro: " + message.message);
        break;
      default:
        break;
    }
  }

  // ------------------------------------------------------------------
  // recording lifecycle
  // ------------------------------------------------------------------
  async function startRecording() {
    if (state.recording) return;
    if (!(await ensureMicrophone())) return;

    const socket = connect();
    const begin = () => {
      state.recording = true;
      state.startedAt = performance.now();
      state.pending = [];
      el.ptt.classList.add("recording");
      el.pttTitle.textContent = "Gravando… solte para processar";
      el.partial.textContent = "—";

      // Send the pre-roll first: it protects the first syllable.
      if (state.preRoll.length) {
        flushFrames(state.preRoll.slice());
        state.preRoll = [];
        state.preRollSamples = 0;
      }

      state.timer = setInterval(() => {
        const seconds = (performance.now() - state.startedAt) / 1000;
        el.pttTimer.textContent = seconds.toFixed(1) + "s";
      }, 100);
    };

    if (socket.readyState === WebSocket.OPEN) {
      socket.send(JSON.stringify({ type: "start" }));
      begin();
    } else {
      socket.addEventListener("open", () => {
        socket.send(JSON.stringify({ type: "start" }));
        begin();
      }, { once: true });
    }
  }

  function stopRecording() {
    if (!state.recording) return;
    state.recording = false;
    clearInterval(state.timer);

    el.ptt.classList.remove("recording");
    el.ptt.classList.add("processing");
    el.pttTitle.textContent = "Processando…";
    setStatus("decodificando");

    // Flush buffered audio, then 400 ms of silence, then stop.
    flushFrames(state.pending);
    state.pending = [];
    sendAudio(new Float32Array((SAMPLE_RATE * TAIL_SILENCE_MS) / 1000));

    if (state.socket && state.socket.readyState === WebSocket.OPEN) {
      state.socket.send(JSON.stringify({ type: "stop" }));
    }
  }

  // ------------------------------------------------------------------
  // rendering
  // ------------------------------------------------------------------
  function renderResult(result) {
    el.ptt.classList.remove("processing");
    el.pttTitle.textContent = "Segure ESPAÇO para falar";
    setStatus("pronto");
    state.lastResult = result;

    el.value.textContent = result.currency || "R$ 0,00";
    el.kind.textContent = result.bid_kind || "—";
    el.mode.textContent = result.value_mode || "—";
    el.latency.textContent = `${Math.round(result.latency_ms || 0)} ms`;
    el.partial.textContent = result.raw_transcript || "—";

    setFlag(el.flagAdd, result.has_addition);
    setFlag(el.flagSum, result.has_sum);
    setFlag(el.flagMult, result.has_multiplication);

    el.numbers.textContent = result.numbers_found && result.numbers_found.length
      ? "números: " + result.numbers_found.map((n) => n.toFixed(2)).join(", ")
      : "";

    renderNBest(result.nbest || []);
    appendLog(result);
  }

  function setFlag(node, value) {
    node.classList.toggle("on", Boolean(value));
    node.querySelector(".flag-value").textContent = value ? "true" : "false";
  }

  function renderNBest(items) {
    if (!items.length) {
      el.nbestPanel.hidden = true;
      return;
    }
    el.nbestPanel.hidden = false;
    el.nbestList.replaceChildren(
      ...items.map((item) => {
        const li = document.createElement("li");
        if (item.chosen) li.classList.add("chosen");
        const score = document.createElement("span");
        score.className = "score";
        score.textContent = Number(item.score).toFixed(2);
        const text = document.createElement("span");
        text.textContent = item.text + (item.chosen ? "  ← escolhido" : "");
        li.append(score, text);
        return li;
      }),
    );
  }

  function formatEntry(result) {
    const lines = [
      `[ASR]  "${result.raw_transcript}"`,
      `[TXT]  "${result.normalized_text}"`,
      `[BID]  ${result.currency}  value=${result.value.toFixed(2)}  mode=${result.value_mode}` +
        `  kind=${result.bid_kind}`,
      `[FLG]  add=${result.has_addition}  sum=${result.has_sum}  mult=${result.has_multiplication}` +
        `  action=${result.action_type}`,
    ];
    if (result.reason) lines.push(`[WHY]  ${result.reason}`);
    lines.push(`[PRF]  conf=${result.confidence.toFixed(2)}  ${Math.round(result.latency_ms)}ms`);
    return lines.join("\n") + "\n" + "-".repeat(52) + "\n";
  }

  function appendLog(result) {
    const entry = formatEntry(result);
    if (el.appendToggle.checked) {
      state.history.push(entry);
    } else {
      state.history = [entry];
    }
    el.log.value = state.history.join("");
    el.log.scrollTop = el.log.scrollHeight;
    updateCounters();
  }

  function updateCounters() {
    const chars = el.log.value.length;
    el.charCount.textContent = `${chars} caractere${chars === 1 ? "" : "s"}`;
  }

  function setStatus(text) {
    el.status.textContent = text;
  }

  // ------------------------------------------------------------------
  // events
  // ------------------------------------------------------------------
  window.addEventListener("keydown", (event) => {
    if (event.code !== "Space" || event.repeat) return;
    if (event.target instanceof HTMLTextAreaElement || event.target instanceof HTMLInputElement) {
      return;
    }
    event.preventDefault();
    if (state.spaceDown) return;
    state.spaceDown = true;
    startRecording();
  });

  window.addEventListener("keyup", (event) => {
    if (event.code !== "Space") return;
    if (!state.spaceDown) return;
    event.preventDefault();
    state.spaceDown = false;
    stopRecording();
  });

  // Pointer fallback for touch devices and for anyone who prefers clicking.
  el.ptt.addEventListener("pointerdown", (event) => {
    event.preventDefault();
    startRecording();
  });
  el.ptt.addEventListener("pointerup", (event) => {
    event.preventDefault();
    stopRecording();
  });
  el.ptt.addEventListener("pointercancel", stopRecording);

  el.copyBtn.addEventListener("click", async () => {
    const text = el.log.value;
    try {
      await navigator.clipboard.writeText(text);
    } catch {
      el.log.select();
      document.execCommand("copy");
    }
    el.copyBtn.classList.add("copied");
    el.copyBtn.textContent = "Copiado!";
    setTimeout(() => {
      el.copyBtn.classList.remove("copied");
      el.copyBtn.textContent = "Copiar tudo";
    }, 1400);
  });

  el.clearBtn.addEventListener("click", () => {
    state.history = [];
    el.log.value = "";
    updateCounters();
    setStatus("histórico limpo");
  });

  el.log.addEventListener("input", updateCounters);
  updateCounters();

  // Warm up the microphone on first interaction so the first bid is not lost.
  window.addEventListener("pointerdown", () => ensureMicrophone(), { once: true });
  window.addEventListener("keydown", () => ensureMicrophone(), { once: true });
})();
