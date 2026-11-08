"use strict";

// Management UI: admin creation, sign-in, the one-time setup wizard, the Guide,
// and Settings (Library and Scheduled Tasks).

const $ = (selector, root = document) => root.querySelector(selector);

const views = ["setup-view", "login-view", "wizard-view", "guide-view", "settings-view"];
const SCAN_TASK = "library-scan";
const POLL_RUNNING_MS = 1500;
const POLL_IDLE_MS = 30000;

function show(viewId) {
  $("#loading").hidden = true;
  for (const id of views) $("#" + id).hidden = id !== viewId;
}

function isVisible(viewId) {
  return !$("#" + viewId).hidden;
}

async function api(method, path, body) {
  const response = await fetch(path, {
    method,
    headers: body ? { "Content-Type": "application/json" } : {},
    body: body ? JSON.stringify(body) : undefined,
    credentials: "same-origin",
  });
  let data = null;
  if (response.status !== 204) {
    try { data = await response.json(); } catch { data = null; }
  }
  const signedInOnly = ["/api/jellyfin", "/api/libraries", "/api/tasks"].some((p) => path.startsWith(p));
  if (response.status === 401 && signedInOnly) {
    await route();
    throw new Error("Your session expired. Sign in again.");
  }
  if (response.status === 429) throw new Error("Too many attempts. Wait a minute and try again.");
  if (!response.ok) throw new Error(data?.error ?? `Request failed (HTTP ${response.status}).`);
  return data;
}

function setMessage(form, kind, text) {
  const error = $(".error", form);
  const success = $(".success", form);
  if (error) { error.hidden = kind !== "error"; error.textContent = kind === "error" ? text : ""; }
  if (success) { success.hidden = kind !== "success"; success.textContent = kind === "success" ? text : ""; }
}

async function busy(buttons, work) {
  buttons.forEach((b) => (b.disabled = true));
  try { return await work(); } finally { buttons.forEach((b) => (b.disabled = false)); }
}

function formatTime(value) {
  return value
    ? new Date(value).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" })
    : "—";
}

function plural(count, noun) {
  return `${count.toLocaleString()} ${noun}${count === 1 ? "" : "s"}`;
}

function cell(text, className) {
  const td = document.createElement("td");
  td.textContent = text;
  if (className) td.className = className;
  return td;
}

// ---- Routing --------------------------------------------------------------

async function route() {
  clearTimeout(pollTimer);
  const session = await api("GET", "/api/session");
  const inApp = session.authenticated && session.setupComplete;
  $("#userbar").hidden = !session.authenticated;
  $("#mainnav").hidden = !inApp;
  $("#current-user").textContent = session.username ?? "";

  if (session.needsSetup) return show("setup-view");
  if (!session.authenticated) return show("login-view");
  if (!session.setupComplete) {
    show("wizard-view");
    return loadWizard();
  }

  const inSettings = location.hash.startsWith("#/settings");
  setActiveLinks("#mainnav a", "section", inSettings ? "settings" : "guide");
  if (!inSettings) return show("guide-view");

  show("settings-view");
  const onTasks = location.hash.startsWith("#/settings/scheduled-tasks");
  setActiveLinks(".subnav a", "sub", onTasks ? "tasks" : "library");
  $("#settings-library").hidden = onTasks;
  $("#settings-tasks").hidden = !onTasks;

  if (onTasks) {
    taskCards.clear();
    $("#tasks-list").replaceChildren();
    await refreshTasks();
  } else {
    await loadLibraryPage();
  }
}

function setActiveLinks(selector, attribute, current) {
  for (const link of document.querySelectorAll(selector)) {
    const active = link.dataset[attribute] === current;
    link.classList.toggle("active", active);
    if (active) link.setAttribute("aria-current", "page"); else link.removeAttribute("aria-current");
  }
}

function libraryPageVisible() {
  return isVisible("settings-view") && !$("#settings-library").hidden;
}

function tasksPageVisible() {
  return isVisible("settings-view") && !$("#settings-tasks").hidden;
}

// The connection and library cards are shared by the wizard and Settings → Library.
function mountLibraryPanels(target) {
  const panels = $("#library-panels");
  if (panels.parentElement !== target) target.append(panels);
}

window.addEventListener("hashchange", () => route().catch(() => {}));

// ---- Setup and sign-in ----------------------------------------------------

$("#setup-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const form = event.currentTarget;
  const { username, password, confirm } = Object.fromEntries(new FormData(form));
  if (password !== confirm) return setMessage(form, "error", "The passwords don't match.");
  try {
    await busy([$("button", form)], () => api("POST", "/api/setup", { username, password }));
    form.reset();
    await route();
  } catch (err) {
    setMessage(form, "error", err.message);
  }
});

$("#login-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const form = event.currentTarget;
  const { username, password } = Object.fromEntries(new FormData(form));
  try {
    await busy([$("button", form)], () => api("POST", "/api/login", { username, password }));
    form.reset();
    setMessage(form, null);
    await route();
  } catch (err) {
    setMessage(form, "error", err.message);
  }
});

$("#logout").addEventListener("click", async () => {
  await api("POST", "/api/logout");
  await route();
});

// ---- Jellyfin connection --------------------------------------------------

const jfForm = $("#jellyfin-form");
let savedConnection = null;

function selectedAuthMethod() {
  return new FormData(jfForm).get("authMethod");
}

function syncAuthFields() {
  const method = selectedAuthMethod();
  for (const block of jfForm.querySelectorAll("[data-auth]")) {
    block.hidden = block.dataset.auth !== method;
  }
  $("#apikey-keep").hidden = !(savedConnection?.configured && savedConnection.authMethod === "apiKey");
}

jfForm.addEventListener("change", (event) => {
  if (event.target.name === "authMethod") syncAuthFields();
});

function formPayload() {
  const data = Object.fromEntries(new FormData(jfForm));
  return data.authMethod === "apiKey"
    ? { url: data.url, authMethod: "apiKey", apiKey: data.apiKey }
    : { url: data.url, authMethod: "password", username: data.username, password: data.password };
}

function renderStatus(status) {
  const badge = $("#conn-badge");
  const connected = status.connected;
  badge.textContent = !status.configured ? "Not connected" : connected ? "Connected" : "Unreachable";
  badge.classList.toggle("ok", connected);

  $("#conn-summary").hidden = !status.configured;
  $("#disconnect-btn").hidden = !status.configured;
  if (!status.configured) return;

  $("#conn-name").textContent = status.server?.name ?? savedConnection?.serverName ?? "—";
  $("#conn-version").textContent = status.server?.version ?? savedConnection?.serverVersion ?? "—";
  $("#conn-url").textContent = savedConnection?.url ?? "";
  $("#conn-auth").textContent = savedConnection?.authMethod === "password"
    ? `Jellyfin user “${savedConnection.username}”`
    : "API key";

  const error = $("#conn-error");
  error.hidden = connected;
  error.textContent = connected ? "" : status.error ?? "";
}

async function loadConnection() {
  savedConnection = await api("GET", "/api/jellyfin");
  if (savedConnection.configured) {
    jfForm.elements.url.value = savedConnection.url;
    jfForm.elements.authMethod.value = savedConnection.authMethod;
    if (savedConnection.authMethod === "password") jfForm.elements.username.value = savedConnection.username ?? "";
  }
  syncAuthFields();
  setMessage(jfForm, null);

  $("#conn-badge").textContent = savedConnection.configured ? "Checking…" : "Not connected";
  renderStatus(await api("GET", "/api/jellyfin/status"));
}

async function loadLibraryPage() {
  mountLibraryPanels($("#settings-library"));
  $("#jellyfin-card").hidden = false;
  $("#save-btn").textContent = "Save";
  $("#library-back").hidden = true;
  await loadConnection();
  await loadLibraries();
}

const jfButtons = () => [$("#test-btn"), $("#save-btn"), $("#disconnect-btn")];

$("#test-btn").addEventListener("click", async () => {
  if (!jfForm.reportValidity()) return;
  setMessage(jfForm, null);
  try {
    const result = await busy(jfButtons(), () => api("POST", "/api/jellyfin/test", formPayload()));
    const names = result.libraries.map((l) => l.name).join(", ");
    setMessage(jfForm, "success",
      `Connected to ${result.server.name} (Jellyfin ${result.server.version}). ` +
      `Found ${result.libraries.length} ${result.libraries.length === 1 ? "library" : "libraries"}` +
      `${names ? `: ${names}` : ""}. Not saved yet.`);
  } catch (err) {
    setMessage(jfForm, "error", err.message);
  }
});

jfForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  setMessage(jfForm, null);
  try {
    const result = await busy(jfButtons(), () => api("PUT", "/api/jellyfin", formPayload()));
    jfForm.elements.apiKey.value = "";
    jfForm.elements.password.value = "";
    if (isVisible("wizard-view")) {
      wizardStep = 2;
      pickerOpen = true;
      return await loadWizard();
    }
    await loadLibraryPage();
    setMessage(jfForm, "success", `Saved. Connected to ${result.server.name}.`);
  } catch (err) {
    setMessage(jfForm, "error", err.message);
  }
});

$("#disconnect-btn").addEventListener("click", async () => {
  if (!confirm("Remove the saved Jellyfin connection? This also clears the library selection and the index.")) return;
  try {
    await busy(jfButtons(), () => api("DELETE", "/api/jellyfin"));
    jfForm.reset();
    pickerOpen = false;
    await loadLibraryPage();
  } catch (err) {
    setMessage(jfForm, "error", err.message);
  }
});

// ---- Library selection and index -------------------------------------------

const libraryForm = $("#library-form");
let libraries = null;
let pickerOpen = false;

const TYPE_LABELS = {
  movies: "Movies", tvshows: "Shows", mixed: "Mixed", music: "Music", musicvideos: "Music videos",
  homevideos: "Home videos", boxsets: "Collections", books: "Books", photos: "Photos",
  livetv: "Live TV", playlists: "Playlists",
};

const typeLabel = (type) => TYPE_LABELS[type] ?? type;

async function loadLibraries() {
  libraries = await api("GET", "/api/libraries");
  renderLibraries();
  await refreshTasks();
}

function renderLibraries() {
  const showCard = libraries.configured && (libraries.reachable || libraries.selectionConfirmed);
  $("#libraries-card").hidden = !showCard;
  if (!showCard) return;

  const picking = libraries.reachable && (!libraries.selectionConfirmed || pickerOpen);
  libraryForm.hidden = !picking;
  $("#library-index").hidden = picking;
  $("#library-cancel").hidden = !libraries.selectionConfirmed || isVisible("wizard-view");

  if (picking) {
    renderPicker();
  } else {
    renderIndex();
  }
}

function renderPicker() {
  setMessage(libraryForm, null);
  $("#libraries-note").textContent = "";
  const list = $("#library-options");
  list.replaceChildren();

  for (const library of libraries.libraries.filter((l) => l.available)) {
    const item = document.createElement("li");
    const label = document.createElement("label");
    if (!library.supported) label.className = "unsupported";

    const box = document.createElement("input");
    box.type = "checkbox";
    box.value = library.id;
    box.disabled = !library.supported;
    // First time through, start with everything that can be indexed ticked.
    box.checked = library.supported && (library.selected || !libraries.selectionConfirmed);

    const text = document.createElement("span");
    text.textContent = library.name;
    const detail = document.createElement("small");
    detail.textContent = library.supported
      ? [typeLabel(library.collectionType), ...library.locations].join(" · ")
      : `${typeLabel(library.collectionType)} · PrimeTime only indexes movies and shows`;
    text.append(detail);

    label.append(box, text);
    item.append(label);
    list.append(item);
  }
}

function renderIndex() {
  const selected = libraries.libraries.filter((l) => l.selected);
  const body = $("#libraries-body");
  body.replaceChildren();

  for (const library of selected) {
    const row = document.createElement("tr");
    let scanned = library.lastScannedAt ? formatTime(library.lastScannedAt) : "Not yet";
    let scannedClass = "";
    if (library.lastScanError) { scanned = library.lastScanError; scannedClass = "warn"; }
    if (!library.available) { scanned = "No longer on the Jellyfin server"; scannedClass = "warn"; }
    row.append(
      cell(library.name),
      cell(typeLabel(library.collectionType)),
      cell(library.movies.toLocaleString(), "num"),
      cell(library.shows.toLocaleString(), "num"),
      cell(library.episodes.toLocaleString(), "num"),
      cell(scanned, scannedClass),
    );
    body.append(row);
  }

  const sum = (key) => selected.reduce((total, l) => total + l[key], 0);
  $("#libraries-note").textContent =
    `${plural(sum("movies"), "movie")} · ${plural(sum("shows"), "show")} · ${plural(sum("episodes"), "episode")}`;

  const excluded = libraries.libraries.filter((l) => !l.selected && l.supported && l.available);
  $("#libraries-excluded").hidden = excluded.length === 0;
  $("#libraries-excluded").textContent = `Not included: ${excluded.map((l) => l.name).join(", ")}.`;
  $("#library-change").hidden = !libraries.reachable;
}

libraryForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  const checked = [...$("#library-options").querySelectorAll("input:checked")].map((box) => box.value);
  if (checked.length === 0) return setMessage(libraryForm, "error", "Select at least one library.");

  const dropped = libraries.libraries.filter((l) => l.selected && !checked.includes(l.id));
  if (dropped.length > 0 &&
      !confirm(`Remove ${dropped.map((l) => l.name).join(", ")} from the index?`)) return;

  try {
    await busy([$('button[type="submit"]', libraryForm)],
      () => api("PUT", "/api/libraries/selection", { libraryIds: checked }));
    pickerOpen = false;
    wizardStep = null;
    await (isVisible("wizard-view") ? loadWizard() : loadLibraries());
  } catch (err) {
    setMessage(libraryForm, "error", err.message);
  }
});

$("#library-change").addEventListener("click", () => {
  pickerOpen = true;
  renderLibraries();
});

$("#library-cancel").addEventListener("click", () => {
  pickerOpen = false;
  renderLibraries();
});

// ---- One-time setup wizard --------------------------------------------------

// Set when the user moves back a step; otherwise the step comes from server state.
let wizardStep = null;

async function loadWizard() {
  mountLibraryPanels($("#wizard-slot"));
  $("#save-btn").textContent = "Save and continue";
  await loadConnection();
  libraries = await api("GET", "/api/libraries");
  renderLibraries();

  const step = wizardStep ?? (
    libraries.selectionConfirmed ? 3 : libraries.configured && libraries.reachable ? 2 : 1);

  $("#jellyfin-card").hidden = step !== 1;
  if (step !== 2) $("#libraries-card").hidden = true;
  $("#library-back").hidden = step !== 2;
  $("#wizard-scan").hidden = step !== 3;
  for (const item of document.querySelectorAll("#wizard-steps li")) {
    const number = Number(item.dataset.step);
    item.classList.toggle("current", number === step);
    item.classList.toggle("done", number < step);
    if (number === step) item.setAttribute("aria-current", "step"); else item.removeAttribute("aria-current");
  }

  if (step === 3) await refreshTasks();
}

function wizardOnScanStep() {
  return isVisible("wizard-view") && !$("#wizard-scan").hidden;
}

async function updateWizardScan() {
  const task = tasksState.tasks.find((t) => t.key === SCAN_TASK);
  const progress = $("#wizard-progress");
  const error = $("#wizard-scan-error");

  if (task.running) {
    $("#wizard-scan-status").textContent = progressText(task.running);
    if (task.running.total) {
      progress.max = task.running.total;
      progress.value = task.running.processed;
    } else {
      progress.removeAttribute("value");
    }
    progress.hidden = false;
    error.hidden = true;
    $("#wizard-scan-actions").hidden = true;
    pollTimer = setTimeout(refreshTasks, POLL_RUNNING_MS);
    return;
  }

  const session = await api("GET", "/api/session");
  if (session.setupComplete) {
    history.replaceState(null, "", "#/guide");
    await route();
    return;
  }

  const lastRun = task.recentRuns[0];
  progress.hidden = true;
  $("#wizard-scan-status").textContent = lastRun ? "The first scan didn't finish." : "The first scan hasn't started.";
  error.hidden = !lastRun?.error;
  error.textContent = lastRun?.error ?? "";
  $("#wizard-scan-actions").hidden = false;
  pollTimer = setTimeout(refreshTasks, POLL_IDLE_MS);
}

function showWizardScanError(message) {
  const error = $("#wizard-scan-error");
  error.hidden = false;
  error.textContent = message;
}

$("#wizard-retry").addEventListener("click", () => runTask(SCAN_TASK, showWizardScanError));

$("#wizard-change").addEventListener("click", () => {
  wizardStep = 2;
  pickerOpen = true;
  loadWizard().catch((err) => showWizardScanError(err.message));
});

$("#library-back").addEventListener("click", () => {
  wizardStep = 1;
  loadWizard().catch((err) => setMessage(libraryForm, "error", err.message));
});

// ---- Tasks and scan status --------------------------------------------------

const STATUS_LABELS = {
  running: "Running", succeeded: "Succeeded", partial: "Finished with errors", failed: "Failed",
  cancelled: "Stopped", interrupted: "Interrupted",
};
const TRIGGER_LABELS = {
  initial: "First scan", selection: "Libraries changed", manual: "Manual", scheduled: "Scheduled",
};

let tasksState = null;
let pollTimer = null;
const taskCards = new Map();

function progressText(running) {
  const count = running.total != null
    ? ` (${running.processed.toLocaleString()} of ${running.total.toLocaleString()})`
    : "";
  return `${running.message ?? "Working…"}${count}`;
}

function nextRunText(task) {
  if (task.running) return "After this run finishes";
  if (task.notReadyReason) return task.notReadyReason;
  if (!task.enabled) return "Off. Runs only when you start it.";
  if (!task.nextRunAt) return "Not scheduled yet";
  return new Date(task.nextRunAt) <= new Date() ? "Any moment now" : formatTime(task.nextRunAt);
}

function lastRunText(run) {
  if (!run) return "Never";
  return `${formatTime(run.finishedAt ?? run.startedAt)}: ${STATUS_LABELS[run.status] ?? run.status}`;
}

async function refreshTasks() {
  clearTimeout(pollTimer);
  const wasRunning = tasksState?.tasks.some((t) => t.running) ?? false;
  try {
    tasksState = await api("GET", "/api/tasks");
  } catch {
    return; // Signed out or server gone; route() or the next page load takes over.
  }
  const running = tasksState.tasks.some((t) => t.running);

  renderScanStatus();
  renderTasks();

  if (isVisible("wizard-view")) {
    if (wizardOnScanStep()) await updateWizardScan();
    return;
  }
  if (wasRunning && !running && libraryPageVisible()) {
    await loadLibraries();
    return;
  }
  if (libraryPageVisible() || tasksPageVisible()) {
    pollTimer = setTimeout(refreshTasks, running ? POLL_RUNNING_MS : POLL_IDLE_MS);
  }
}

function renderScanStatus() {
  const task = tasksState.tasks.find((t) => t.key === SCAN_TASK);
  if (!task) return;

  const lastRun = task.running ? null : task.recentRuns[0];
  const status = $("#scan-status");
  status.classList.toggle("running", Boolean(task.running));
  if (task.running) {
    status.textContent = `Scanning: ${progressText(task.running)}`;
  } else if (lastRun) {
    const next = task.enabled && !task.notReadyReason ? ` Next scan: ${nextRunText(task)}.` : "";
    status.textContent = `${lastRunText(lastRun)}. ${lastRun.summary ?? ""}${next}`;
  } else {
    status.textContent = "No scan has run yet.";
  }

  const error = $("#scan-error");
  error.hidden = !lastRun?.error;
  error.textContent = lastRun?.error ?? "";

  $("#scan-now").hidden = Boolean(task.running);
  $("#scan-cancel").hidden = !task.running;
}

$("#scan-now").addEventListener("click", () => runTask(SCAN_TASK, (message) => {
  const error = $("#scan-error");
  error.hidden = false;
  error.textContent = message;
}));

$("#scan-cancel").addEventListener("click", () => cancelTask(SCAN_TASK));

async function runTask(key, onError) {
  try {
    await api("POST", `/api/tasks/${key}/run`);
    await refreshTasks();
  } catch (err) {
    onError(err.message);
  }
}

async function cancelTask(key) {
  try {
    await api("POST", `/api/tasks/${key}/cancel`);
  } catch {
    // It finished on its own; the refresh below shows that.
  }
  await refreshTasks();
}

function intervalParts(minutes) {
  if (minutes % 1440 === 0) return { every: minutes / 1440, unit: "1440" };
  if (minutes % 60 === 0) return { every: minutes / 60, unit: "60" };
  return { every: minutes, unit: "1" };
}

function buildTaskCard(task) {
  const card = $("#task-template").content.firstElementChild.cloneNode(true);
  $('[data-field="name"]', card).textContent = task.name;
  $('[data-field="description"]', card).textContent = task.description;

  const form = $("form", card);
  const fillForm = (source) => {
    const { every, unit } = intervalParts(source.intervalMinutes);
    form.elements.enabled.checked = source.enabled;
    form.elements.every.value = every;
    form.elements.unit.value = unit;
  };
  fillForm(task);

  form.addEventListener("submit", async (event) => {
    event.preventDefault();
    setMessage(form, null);
    const intervalMinutes = Number(form.elements.every.value) * Number(form.elements.unit.value);
    if (!Number.isInteger(intervalMinutes) ||
        intervalMinutes < tasksState.minIntervalMinutes || intervalMinutes > tasksState.maxIntervalMinutes) {
      return setMessage(form, "error", "Choose an interval between 15 minutes and 30 days.");
    }
    try {
      const saved = await busy([$('button[type="submit"]', form)], () => api("PUT", `/api/tasks/${task.key}`, {
        enabled: form.elements.enabled.checked,
        intervalMinutes,
      }));
      fillForm(saved);
      await refreshTasks();
      setMessage(form, "success", "Schedule saved.");
    } catch (err) {
      setMessage(form, "error", err.message);
    }
  });

  $('[data-action="run"]', card).addEventListener("click", () => {
    setMessage(form, null);
    runTask(task.key, (message) => setMessage(form, "error", message));
  });
  $('[data-action="cancel"]', card).addEventListener("click", () => cancelTask(task.key));

  $("#tasks-list").append(card);
  taskCards.set(task.key, card);
  return card;
}

function renderTasks() {
  if (!tasksPageVisible()) return;

  for (const task of tasksState.tasks) {
    const card = taskCards.get(task.key) ?? buildTaskCard(task);
    const field = (name) => $(`[data-field="${name}"]`, card);

    const state = field("state");
    const on = task.enabled && !task.notReadyReason;
    state.textContent = task.running ? "Running" : task.notReadyReason ? "Waiting" : task.enabled ? "On" : "Off";
    state.classList.toggle("ok", Boolean(task.running) || on);

    field("next").textContent = nextRunText(task);
    const finished = task.recentRuns.find((r) => r.status !== "running");
    field("last").textContent = finished
      ? `${lastRunText(finished)}${finished.summary ? `. ${finished.summary}` : ""}`
      : "Never";

    const progress = field("progress");
    progress.hidden = !task.running;
    progress.classList.toggle("running", Boolean(task.running));
    progress.textContent = task.running ? progressText(task.running) : "";

    $('[data-action="run"]', card).hidden = Boolean(task.running);
    $('[data-action="run"]', card).disabled = Boolean(task.notReadyReason);
    $('[data-action="cancel"]', card).hidden = !task.running;

    const rows = field("runs");
    rows.replaceChildren();
    for (const run of task.recentRuns) {
      const row = document.createElement("tr");
      const failed = run.status === "failed" || run.status === "partial";
      row.append(
        cell(formatTime(run.startedAt)),
        cell(TRIGGER_LABELS[run.trigger] ?? run.trigger),
        cell(STATUS_LABELS[run.status] ?? run.status, failed ? "warn" : ""),
        cell([run.summary, run.error].filter(Boolean).join(" ") || "—"),
      );
      rows.append(row);
    }
    field("no-runs").hidden = task.recentRuns.length > 0;
    rows.closest("table").hidden = task.recentRuns.length === 0;
  }
}

route().catch((err) => {
  $("#loading").textContent = `Couldn't reach the PrimeTime server: ${err.message}`;
});
