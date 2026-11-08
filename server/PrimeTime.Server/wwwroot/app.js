"use strict";

// Management UI: admin creation, sign-in, the one-time setup wizard, the Guide,
// and Settings (Library and Scheduled Tasks).

const $ = (selector, root = document) => root.querySelector(selector);

const views = [
  "setup-view", "login-view", "wizard-view", "guide-view", "add-channel-view", "channel-editor-view", "settings-view", "player-view", "link-view",
];
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
  const isFile = body instanceof Blob;
  const response = await fetch(path, {
    method,
    headers: isFile ? { "Content-Type": body.type || "application/octet-stream" }
      : body ? { "Content-Type": "application/json" } : {},
    body: isFile ? body : body ? JSON.stringify(body) : undefined,
    credentials: "same-origin",
  });
  let data = null;
  if (response.status !== 204) {
    try { data = await response.json(); } catch { data = null; }
  }
  const signedInOnly = ["/api/jellyfin", "/api/libraries", "/api/tasks", "/api/channels", "/api/catalog", "/api/guide", "/api/account", "/api/play", "/api/devices"].some((p) => path.startsWith(p));
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
  clearTimeout(guideTimer);
  if (!location.hash.startsWith("#/watch/")) leavePlayer();
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

  if (location.hash.startsWith("#/link")) return openLink();

  const inSettings = location.hash.startsWith("#/settings");
  setActiveLinks("#mainnav a", "section", inSettings ? "settings" : "guide");
  if (!inSettings) return routeGuide();

  show("settings-view");
  const sub = location.hash.startsWith("#/settings/scheduled-tasks") ? "tasks"
    : location.hash.startsWith("#/settings/account") ? "account"
    : location.hash.startsWith("#/settings/devices") ? "devices" : "library";
  setActiveLinks(".subnav a", "sub", sub);
  $("#settings-library").hidden = sub !== "library";
  $("#settings-tasks").hidden = sub !== "tasks";
  $("#settings-account").hidden = sub !== "account";
  $("#settings-devices").hidden = sub !== "devices";

  if (sub === "tasks") {
    taskCards.clear();
    $("#tasks-list").replaceChildren();
    await refreshTasks();
  } else if (sub === "library") {
    await loadLibraryPage();
  } else if (sub === "devices") {
    await loadDevices();
  } else {
    $("#password-form").reset();
    setMessage($("#password-form"), null);
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

$("#password-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const form = event.currentTarget;
  const { currentPassword, newPassword, confirm } = Object.fromEntries(new FormData(form));
  if (newPassword !== confirm) return setMessage(form, "error", "The new passwords don't match.");
  try {
    await busy([$("button", form)], () => api("POST", "/api/account/password", { currentPassword, newPassword }));
    form.reset();
    setMessage(form, "success", "Password changed. Your other devices have been signed out.");
  } catch (err) {
    setMessage(form, "error", err.message);
  }
});

// ---- Linking apps -------------------------------------------------------------

let linkCode = null;

function showLinkStep(step) {
  $("#link-form").hidden = step !== "form";
  $("#link-confirm").hidden = step !== "confirm";
  $("#link-done").hidden = step !== "done";
}

function showLinkDone(message) {
  $("#link-result").textContent = message;
  showLinkStep("done");
}

async function reviewLink(code) {
  const pending = await api("GET", `/api/devices/link?code=${encodeURIComponent(code)}`);
  linkCode = code;
  $("#link-device").textContent = pending.platform ? `${pending.name} (${pending.platform})` : pending.name;
  $("#link-code").textContent = code.toUpperCase();
  $("#link-error").hidden = true;
  showLinkStep("confirm");
}

async function openLink() {
  show("link-view");
  $("#link-form").reset();
  setMessage($("#link-form"), null);
  showLinkStep("form");
  const code = new URLSearchParams(location.hash.split("?")[1] ?? "").get("code");
  if (!code) return;
  $("#link-form").elements.code.value = code;
  try {
    await reviewLink(code);
  } catch (err) {
    setMessage($("#link-form"), "error", err.message);
  }
}

$("#link-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const form = event.currentTarget;
  setMessage(form, null);
  try {
    await busy([$("button", form)], () => reviewLink(form.elements.code.value.trim()));
  } catch (err) {
    setMessage(form, "error", err.message);
  }
});

async function decideLink(action, done) {
  const buttons = [$("#link-approve"), $("#link-deny")];
  try {
    await busy(buttons, () => api("POST", `/api/devices/link/${action}`, { code: linkCode }));
    showLinkDone(done);
  } catch (err) {
    $("#link-error").hidden = false;
    $("#link-error").textContent = err.message;
  }
}

$("#link-approve").addEventListener("click", () => decideLink("approve", "Device linked. You can go back to it now."));
$("#link-deny").addEventListener("click", () => decideLink("deny", "Cancelled. The device wasn't linked."));

async function loadDevices() {
  $("#devices-error").hidden = true;
  const devices = await api("GET", "/api/devices");
  const list = $("#device-list");
  list.replaceChildren();
  $("#devices-empty").hidden = devices.length > 0;
  for (const device of devices) {
    const item = document.createElement("li");
    const text = document.createElement("div");
    text.textContent = device.name;
    const detail = document.createElement("small");
    detail.textContent = [device.platform, `Last used ${formatTime(device.lastSeenAt * 1000)}`].filter(Boolean).join(" · ");
    text.append(detail);

    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "danger link";
    remove.textContent = "Remove";
    remove.setAttribute("aria-label", `Remove ${device.name}`);
    remove.addEventListener("click", async () => {
      if (!confirm(`Remove ${device.name}? It will have to be linked again.`)) return;
      try {
        await api("DELETE", `/api/devices/${device.id}`);
        await loadDevices();
      } catch (err) {
        $("#devices-error").hidden = false;
        $("#devices-error").textContent = err.message;
      }
    });

    item.append(text, remove);
    list.append(item);
  }
}

// ---- Theme ------------------------------------------------------------------

const THEME_KEY = "primetime.theme";
const systemDark = matchMedia("(prefers-color-scheme: dark)");

function savedTheme() {
  try {
    const theme = localStorage.getItem(THEME_KEY);
    return theme === "light" || theme === "dark" ? theme : null;
  } catch {
    return null; // Storage can be blocked; the toggle still works until the page reloads.
  }
}

function applyTheme(theme) {
  document.documentElement.dataset.theme = theme;
  const label = `Switch to ${theme === "dark" ? "light" : "dark"} mode`;
  $("#theme-toggle").title = label;
  $("#theme-toggle").setAttribute("aria-label", label);
}

applyTheme(document.documentElement.dataset.theme);

// Follow the device until a choice has been made here.
systemDark.addEventListener("change", (event) => {
  if (!savedTheme()) applyTheme(event.matches ? "dark" : "light");
});

$("#theme-toggle").addEventListener("click", () => {
  const theme = document.documentElement.dataset.theme === "dark" ? "light" : "dark";
  try { localStorage.setItem(THEME_KEY, theme); } catch { /* Applies to this page load only. */ }
  applyTheme(theme);
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
    detail.textContent = library.collectionType === "boxsets"
      ? "Collections you manage in Jellyfin. They play movies and shows from the libraries you include."
      : library.supported
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
    const isCollections = library.collectionType === "boxsets";
    const count = (value) => cell(isCollections ? "—" : value.toLocaleString(), "num");
    row.append(
      cell(library.name),
      cell(isCollections ? plural(library.collections, "collection") : typeLabel(library.collectionType)),
      count(library.movies),
      count(library.shows),
      count(library.episodes),
      cell(scanned, scannedClass),
    );
    body.append(row);
  }

  const sum = (key) => selected.reduce((total, l) => total + l[key], 0);
  const totals = [plural(sum("movies"), "movie"), plural(sum("shows"), "show"), plural(sum("episodes"), "episode")];
  if (selected.some((l) => l.collectionType === "boxsets")) totals.push(plural(sum("collections"), "collection"));
  $("#libraries-note").textContent = totals.join(" · ");

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

// ---- Guide ------------------------------------------------------------------

const PIXELS_PER_MINUTE = 4;
const HALF_HOUR = 1800;
const GUIDE_WINDOW = 8 * 3600;
const GUIDE_STEP = 4 * 3600;
const GUIDE_REFRESH_MS = 60000;
const GUIDE_LOOKBACK = 24 * 3600;
const GUIDE_LOOKAHEAD = 7 * 24 * 3600;

let guideStart = null;
let guideTimer = null;

function nowSeconds() {
  return Math.floor(Date.now() / 1000);
}

function currentGuideStart() {
  const now = nowSeconds();
  return now - (now % HALF_HOUR) - HALF_HOUR;
}

function routeGuide() {
  const watching = location.hash.match(/^#\/watch\/(\d+)$/);
  if (watching) {
    show("player-view");
    return tune(Number(watching[1]));
  }
  if (location.hash === "#/guide/add") {
    show("add-channel-view");
    return loadSuggestions();
  }
  const match = location.hash.match(/^#\/guide\/channels\/(new|\d+)$/);
  if (match) {
    show("channel-editor-view");
    return openEditor(match[1] === "new" ? null : Number(match[1]));
  }
  show("guide-view");
  return loadGuide();
}

async function loadGuide() {
  clearTimeout(guideTimer);
  guideStart ??= currentGuideStart();
  const guide = await api("GET", `/api/guide?from=${guideStart}&to=${guideStart + GUIDE_WINDOW}`);

  const hasChannels = guide.channels.length > 0;
  $("#guide-empty").hidden = hasChannels;
  $("#guide-board").hidden = !hasChannels;
  $("#guide-tools").hidden = !hasChannels;
  if (hasChannels) renderGuide(guide);

  guideTimer = setTimeout(() => {
    if (isVisible("guide-view")) loadGuide().catch(() => {});
  }, GUIDE_REFRESH_MS);
}

function clockLabel(seconds) {
  return new Date(seconds * 1000).toLocaleTimeString(undefined, { hour: "numeric", minute: "2-digit" });
}

function guideOffset(seconds) {
  return ((seconds - guideStart) / 60) * PIXELS_PER_MINUTE;
}

function renderGuide(guide) {
  const inner = $("#guide-inner");
  const trackWidth = (GUIDE_WINDOW / 60) * PIXELS_PER_MINUTE;
  const windowEnd = guideStart + GUIDE_WINDOW;
  inner.replaceChildren();
  inner.style.width = `calc(var(--guide-label) + ${trackWidth}px)`;

  $("#guide-date").textContent = new Date(guideStart * 1000)
    .toLocaleDateString(undefined, { weekday: "long", month: "long", day: "numeric" });
  $("#guide-earlier").disabled = guideStart - GUIDE_STEP < guide.now - GUIDE_LOOKBACK;
  $("#guide-later").disabled = guideStart + GUIDE_STEP > guide.now + GUIDE_LOOKAHEAD - GUIDE_WINDOW;

  const makeTrack = () => {
    const track = document.createElement("div");
    track.className = "guide-track";
    track.style.width = `${trackWidth}px`;
    return track;
  };

  const header = document.createElement("div");
  header.className = "guide-row guide-times";
  const corner = document.createElement("div");
  corner.className = "guide-channel";
  const times = makeTrack();
  for (let t = guideStart; t < windowEnd; t += HALF_HOUR) {
    const label = document.createElement("span");
    label.className = "guide-time";
    label.style.left = `${guideOffset(t)}px`;
    label.textContent = clockLabel(t);
    times.append(label);
  }
  header.append(corner, times);
  inner.append(header);

  for (const channel of guide.channels) {
    const row = document.createElement("div");
    row.className = "guide-row";

    const cell = document.createElement("div");
    cell.className = "guide-channel";
    const link = document.createElement("a");
    link.className = "guide-watch";
    link.href = `#/watch/${channel.id}`;
    link.title = `Watch ${channel.name}`;
    const edit = document.createElement("a");
    edit.className = "guide-edit";
    edit.href = `#/guide/channels/${channel.id}`;
    edit.title = `Edit ${channel.name}`;
    edit.setAttribute("aria-label", `Edit ${channel.name}`);
    edit.textContent = "✎";
    cell.append(link, edit);
    const number = document.createElement("b");
    number.textContent = channel.number;
    link.append(number);
    if (channel.logoUrl) {
      const logo = document.createElement("div");
      logo.className = "guide-logo";
      const image = document.createElement("img");
      image.src = channel.logoUrl;
      image.alt = channel.name;
      logo.append(image);
      link.append(logo);
    } else {
      const name = document.createElement("span");
      name.textContent = channel.name;
      link.append(name);
    }

    const track = makeTrack();
    for (const program of channel.programs) {
      const left = Math.max(0, guideOffset(program.start));
      const right = Math.min(trackWidth, guideOffset(program.end));
      const block = document.createElement("div");
      block.className = "guide-program";
      const onAir = program.start <= guide.now && guide.now < program.end;
      block.classList.toggle("on-air", onAir);
      if (onAir) block.addEventListener("click", () => { location.hash = `#/watch/${channel.id}`; });
      if (channel.color) block.style.setProperty("--channel", channel.color);
      block.style.left = `${left}px`;
      block.style.width = `${Math.max(0, right - left - 2)}px`;
      block.title = [program.title, program.subtitle, `${clockLabel(program.start)} – ${clockLabel(program.end)}`]
        .filter(Boolean).join("\n");
      const title = document.createElement("strong");
      title.textContent = program.title;
      const subtitle = document.createElement("small");
      subtitle.textContent = program.subtitle ?? "";
      block.append(title, subtitle);
      track.append(block);
    }
    if (channel.programs.length === 0) {
      const none = document.createElement("span");
      none.className = "guide-none";
      none.textContent = "Nothing scheduled";
      track.append(none);
    }

    row.append(cell, track);
    inner.append(row);
  }

  if (guide.now >= guideStart && guide.now < windowEnd) {
    const line = document.createElement("div");
    line.className = "guide-now-line";
    line.style.left = `calc(var(--guide-label) + ${guideOffset(guide.now)}px)`;
    inner.append(line);
  }
}

function moveGuide(start) {
  guideStart = start;
  $("#guide-scroll").scrollLeft = 0;
  loadGuide().catch(() => {});
}

$("#guide-earlier").addEventListener("click", () => moveGuide(guideStart - GUIDE_STEP));
$("#guide-later").addEventListener("click", () => moveGuide(guideStart + GUIDE_STEP));
$("#guide-now").addEventListener("click", () => moveGuide(currentGuideStart()));

// ---- Player -------------------------------------------------------------------

const playerVideo = $("#player-video");
const MAX_LEADING_GAP = 10;
let stream = null; // What's playing now: its ID, player, program and whether it's following the live position.
let tuneRequest = 0;
let tuneRetries = 0;

const infoPanel = $("#player-info");
let infoRequest = 0;

function setInfoOpen(open) {
  infoPanel.hidden = !open;
  $("#player-info-toggle").setAttribute("aria-expanded", String(open));
  if (!open) infoRequest++; // Ignore any answer still on its way.
}

function infoElement(tag, text, className) {
  const element = document.createElement(tag);
  element.textContent = text;
  if (className) element.className = className;
  return element;
}

function runtimeLabel(minutes) {
  if (minutes < 60) return `${minutes}m`;
  return minutes % 60 ? `${Math.floor(minutes / 60)}h ${minutes % 60}m` : `${Math.floor(minutes / 60)}h`;
}

function renderInfo(info) {
  const episodeNumber = info.season != null && info.episode != null ? `S${info.season}E${info.episode}` : null;
  // Jellyfin keeps air dates as whole days, so reading them in UTC gives the right day everywhere.
  const aired = info.airDate
    ? new Date(info.airDate).toLocaleDateString(undefined, { dateStyle: "medium", timeZone: "UTC" })
    : null;
  const meta = [
    episodeNumber ? aired : info.year,
    info.officialRating,
    info.runtimeMinutes ? runtimeLabel(info.runtimeMinutes) : null,
    info.communityRating ? `★ ${info.communityRating.toFixed(1)}` : null,
  ].filter(Boolean);

  const parts = [infoElement("h2", info.show ? `${info.show}` : info.title)];
  if (info.show) parts.push(infoElement("p", [episodeNumber, info.title].filter(Boolean).join(" · "), "player-info-episode"));
  if (meta.length > 0) parts.push(infoElement("p", meta.join(" · "), "muted"));
  if (info.tagline) parts.push(infoElement("p", info.tagline, "player-info-tagline"));
  parts.push(infoElement("p", info.overview || "Jellyfin has no description for this program."));
  if (info.showOverview) {
    parts.push(infoElement("h3", "About the show"), infoElement("p", info.showOverview));
  }

  const facts = document.createElement("dl");
  facts.className = "facts";
  const addFact = (label, value) => { if (value) facts.append(infoElement("dt", label), infoElement("dd", value)); };
  addFact("Genres", info.genres.join(", "));
  addFact(info.show ? "Network" : "Studio", info.studios.join(", "));
  addFact(info.directors.length > 1 ? "Directors" : "Director", info.directors.join(", "));
  addFact("Cast", info.cast.map((c) => (c.role ? `${c.name} (${c.role})` : c.name)).join(", "));
  if (facts.children.length > 0) parts.push(facts);

  infoPanel.replaceChildren(...parts);
}

async function loadInfo() {
  if (!stream) return;
  const request = ++infoRequest;
  infoPanel.replaceChildren(infoElement("p", "Loading…", "muted"));
  try {
    const info = await api("GET", `/api/play/${stream.playId}/info`);
    if (request === infoRequest) renderInfo(info);
  } catch (err) {
    if (request === infoRequest) infoPanel.replaceChildren(infoElement("p", err.message, "error"));
  }
}

$("#player-info-toggle").addEventListener("click", () => {
  const open = infoPanel.hidden;
  setInfoOpen(open);
  if (open) loadInfo();
});

function showPlayerError(message) {
  $("#player-error").hidden = !message;
  $("#player-error").textContent = message ?? "";
}

function stopStream() {
  if (!stream) return;
  clearTimeout(stream.liveTimer);
  stream.hls?.destroy();
  playerVideo.removeAttribute("src");
  playerVideo.load();
  // Tells Jellyfin to stop encoding. keepalive lets it finish even if the tab is closing.
  fetch(`/api/play/${stream.playId}`, { method: "DELETE", keepalive: true, credentials: "same-origin" }).catch(() => {});
  stream = null;
}

function leavePlayer() {
  setInfoOpen(false);
  tuneRequest++;
  tuneRetries = 0;
  stopStream();
}

function serverNow() {
  return nowSeconds() + stream.clockOffset;
}

function setLive(live) {
  stream.live = live;
  clearTimeout(stream.liveTimer);
  $("#player-restart").hidden = !live;
  $("#player-live").hidden = live;
  if (live) {
    // On to whatever's next when this program's slot ends, as on real TV.
    const remaining = stream.program.end - serverNow();
    stream.liveTimer = setTimeout(() => tune(stream.channelId), (remaining + 1) * 1000);
  }
}

function renderNowPlaying(start) {
  const channel = $("#player-channel");
  channel.replaceChildren();
  if (start.channel.logoUrl) {
    const logo = document.createElement("img");
    logo.src = start.channel.logoUrl;
    logo.alt = start.channel.name;
    channel.append(logo);
  } else {
    channel.textContent = `${start.channel.number} ${start.channel.name}`;
  }
  $("#player-title").textContent = start.program.title;
  $("#player-detail").textContent = [start.program.subtitle, `${clockLabel(start.program.start)} – ${clockLabel(start.program.end)}`]
    .filter(Boolean).join(" · ");
}

function startVideo() {
  // Browsers can refuse to start with sound; the player's own controls still work then.
  playerVideo.play().catch(() => {});
}

// A stream's first frame can land a moment after zero, and a seek to zero would then wait forever.
function skipLeadingGap() {
  if (!playerVideo.seeking) return;
  for (let i = 0; i < playerVideo.buffered.length; i++) {
    const start = playerVideo.buffered.start(i);
    if (start > playerVideo.currentTime && start - playerVideo.currentTime < MAX_LEADING_GAP) {
      playerVideo.currentTime = start;
      return;
    }
  }
}

function attachStream(url, offset) {
  if (window.Hls?.isSupported()) {
    // The worker is off so the security policy doesn't need to allow scripts from blob: URLs.
    const hls = new Hls({ enableWorker: false, startPosition: offset });
    stream.hls = hls;
    hls.on(Hls.Events.MANIFEST_PARSED, startVideo);
    hls.on(Hls.Events.FRAG_BUFFERED, skipLeadingGap);
    hls.on(Hls.Events.ERROR, (_, data) => { if (data.fatal) handleStreamFailure(); });
    hls.loadSource(url);
    hls.attachMedia(playerVideo);
  } else if (playerVideo.canPlayType("application/vnd.apple.mpegurl")) {
    playerVideo.src = url; // Safari plays HLS itself.
    playerVideo.addEventListener("loadedmetadata", () => {
      playerVideo.currentTime = offset;
      startVideo();
    }, { once: true });
  } else {
    showPlayerError("This browser can't play the stream.");
  }
}

function handleStreamFailure() {
  if (!stream) return;
  const { channelId } = stream;
  if (tuneRetries++ < 1) {
    tune(channelId); // Often just a stream that went idle during a long pause.
  } else {
    stopStream();
    showPlayerError("The stream stopped. Check that Jellyfin is running, then try again.");
  }
}

async function tune(channelId) {
  const request = ++tuneRequest;
  stopStream();
  showPlayerError(null);
  $("#player-restart").hidden = true;
  $("#player-live").hidden = true;

  let start;
  try {
    start = await api("POST", `/api/channels/${channelId}/play`);
  } catch (err) {
    if (request === tuneRequest) showPlayerError(err.message);
    return;
  }
  if (request !== tuneRequest) { // Changed channel or left while this was on its way.
    fetch(`/api/play/${start.playId}`, { method: "DELETE", credentials: "same-origin" }).catch(() => {});
    return;
  }

  stream = {
    channelId,
    playId: start.playId,
    program: start.program,
    clockOffset: start.now - nowSeconds(),
    hls: null,
    liveTimer: null,
    live: true,
  };
  renderNowPlaying(start);
  setLive(true);
  attachStream(start.url, start.offsetSeconds);
  if (!infoPanel.hidden) loadInfo(); // Still open from the last program, so show this one.
}

playerVideo.addEventListener("playing", () => { tuneRetries = 0; });
playerVideo.addEventListener("ended", () => { if (stream) tune(stream.channelId); });
playerVideo.addEventListener("error", () => { if (stream && !stream.hls && playerVideo.error) handleStreamFailure(); });

$("#player-restart").addEventListener("click", () => {
  if (!stream) return;
  setLive(false);
  playerVideo.currentTime = 0;
  startVideo();
});

$("#player-live").addEventListener("click", () => {
  if (!stream) return;
  if (serverNow() >= stream.program.end) return void tune(stream.channelId);
  playerVideo.currentTime = serverNow() - stream.program.start;
  setLive(true);
  startVideo();
});

window.addEventListener("pagehide", () => stopStream());

// ---- Suggested channels -------------------------------------------------------

function showSuggestionsError(message) {
  const error = $("#suggestions-error");
  error.hidden = !message;
  error.textContent = message ?? "";
}

async function loadSuggestions() {
  showSuggestionsError(null);
  const suggestions = await api("GET", "/api/channels/suggestions");
  const container = $("#suggestions");
  container.replaceChildren();

  if (suggestions.length === 0) {
    const none = document.createElement("p");
    none.className = "muted";
    none.textContent = "No more suggestions for this library. You can still build your own.";
    container.append(none);
    return;
  }

  const groups = Map.groupBy(suggestions, (s) => s.group);
  for (const [group, items] of groups) {
    const section = document.createElement("section");
    section.className = "card suggestion-group";
    const heading = document.createElement("h2");
    heading.textContent = group;
    section.append(heading);

    for (const suggestion of items) {
      const row = document.createElement("div");
      row.className = "suggestion";
      const text = document.createElement("div");
      text.textContent = suggestion.name;
      const detail = document.createElement("small");
      detail.textContent = suggestion.detail;
      text.append(detail);

      const add = document.createElement("button");
      add.type = "button";
      add.className = "secondary";
      add.textContent = "Add";
      add.setAttribute("aria-label", `Add ${suggestion.name}`);
      add.addEventListener("click", async () => {
        showSuggestionsError(null);
        add.disabled = true;
        try {
          await api("POST", "/api/channels", { name: suggestion.name, ...suggestion.definition });
          add.textContent = "Added";
        } catch (err) {
          add.disabled = false;
          showSuggestionsError(err.message);
        }
      });

      row.append(text, add);
      section.append(row);
    }
    container.append(section);
  }
}

// ---- Channel builder -----------------------------------------------------------

const TITLES_SHOWN = 200;
const channelForm = $("#channel-form");
let airOrderChosen = false;
const MAX_LOGO_BYTES = 2 * 1024 * 1024;
const LOGO_TYPES = ["image/png", "image/jpeg", "image/webp", "image/gif"];
let automaticColor = null;
let logoFile = null; // A newly chosen image, not uploaded until the channel is saved.
let logoRemoved = false;
let logoObjectUrl = null;

function showLogo(url) {
  const image = $("#logo-image");
  image.hidden = !url;
  if (url) image.src = url; else image.removeAttribute("src");
  $("#logo-none").hidden = Boolean(url);
  $("#logo-remove").hidden = !url;
}

function setLogoPreview(file) {
  if (logoObjectUrl) URL.revokeObjectURL(logoObjectUrl);
  logoObjectUrl = file ? URL.createObjectURL(file) : null;
  showLogo(logoObjectUrl ?? (logoRemoved ? null : editingChannel?.logoUrl ?? null));
}

$("#logo-choose").addEventListener("click", () => $("#logo-file").click());

$("#logo-file").addEventListener("change", (event) => {
  const file = event.target.files[0];
  event.target.value = ""; // So choosing the same file again still fires this.
  if (!file) return;
  setMessage(channelForm, null);
  if (!LOGO_TYPES.includes(file.type)) return setMessage(channelForm, "error", "Logos need to be a PNG, JPEG, WebP or GIF image.");
  if (file.size > MAX_LOGO_BYTES) return setMessage(channelForm, "error", "Logos can be up to 2 MB.");
  logoFile = file;
  logoRemoved = false;
  setLogoPreview(file);
});

$("#logo-remove").addEventListener("click", () => {
  logoFile = null;
  logoRemoved = true;
  setLogoPreview(null);
});
const catalogs = new Map();
let editingChannel = null;
let pickedTitles = new Set();
let previewTimer = null;
let previewRequest = 0;

async function loadCatalog(contentType) {
  if (!catalogs.has(contentType)) {
    catalogs.set(contentType, await api("GET", `/api/catalog?contentType=${contentType}`));
  }
  return catalogs.get(contentType);
}

function currentChannelBody() {
  const data = new FormData(channelForm);
  const optional = (name) => (data.get(name) ? Number(data.get(name)) : null);
  const checkedIn = (selector) => [...$(selector).querySelectorAll("input:checked")].map((box) => box.value);
  const mode = data.get("mode");
  return {
    name: data.get("name"),
    number: optional("number"),
    contentType: data.get("contentType"),
    mode,
    yearFrom: mode === "filter" ? optional("yearFrom") : null,
    yearBefore: mode === "filter" ? optional("yearBefore") : null,
    genres: mode === "filter" ? checkedIn("#genre-options") : [],
    studios: mode === "filter" ? checkedIn("#studio-options") : [],
    itemIds: mode === "pick" ? [...pickedTitles] : mode === "collection" ? checkedIn("#collection-options") : [],
    airOrder: data.get("airOrder"),
    color: data.get("colorMode") === "custom" ? data.get("color") : null,
  };
}

function renderChips(container, facets, selected) {
  container.replaceChildren();
  // Keep earlier choices visible even if they've dropped out of the most common values.
  const extra = selected.filter((value) => !facets.some((f) => f.name.toLowerCase() === value.toLowerCase()));
  for (const facet of [...extra.map((name) => ({ name, count: 0 })), ...facets]) {
    const label = document.createElement("label");
    const box = document.createElement("input");
    box.type = "checkbox";
    box.value = facet.name;
    box.checked = selected.some((value) => value.toLowerCase() === facet.name.toLowerCase());
    const text = document.createTextNode(` ${facet.name} `);
    const count = document.createElement("small");
    count.textContent = facet.count || "";
    label.append(box, text, count);
    container.append(label);
  }
}

function renderTitleList() {
  const contentType = new FormData(channelForm).get("contentType");
  const catalog = catalogs.get(contentType);
  if (!catalog) return;

  const query = $("#title-search").value.trim().toLowerCase();
  const matches = catalog.items.filter((item) => item.title.toLowerCase().includes(query));
  const shown = matches.slice(0, TITLES_SHOWN);
  const list = $("#title-options");
  list.replaceChildren();

  for (const item of shown) {
    const row = document.createElement("li");
    const label = document.createElement("label");
    const box = document.createElement("input");
    box.type = "checkbox";
    box.value = item.id;
    box.checked = pickedTitles.has(item.id);
    box.addEventListener("change", () => {
      if (box.checked) pickedTitles.add(item.id); else pickedTitles.delete(item.id);
      updateTitleCount(matches.length, shown.length);
      schedulePreview();
    });
    const text = document.createElement("span");
    text.textContent = item.title;
    const detail = document.createElement("small");
    detail.textContent = [item.year, item.episodeCount ? plural(item.episodeCount, "episode") : null]
      .filter(Boolean).join(" · ");
    text.append(detail);
    label.append(box, text);
    row.append(label);
    list.append(row);
  }
  updateTitleCount(matches.length, shown.length);
}

function updateTitleCount(matching, shown) {
  const limited = shown < matching ? ` Showing the first ${shown} of ${matching.toLocaleString()}; search to narrow it down.` : "";
  $("#title-count").textContent = `${pickedTitles.size.toLocaleString()} selected.${limited}`;
}

function renderCollectionList(catalog, contentType, selected) {
  const list = $("#collection-options");
  list.replaceChildren();
  for (const collection of catalog.collections) {
    const row = document.createElement("li");
    const label = document.createElement("label");
    const box = document.createElement("input");
    box.type = "checkbox";
    box.value = collection.id;
    box.checked = selected.includes(collection.id);
    const text = document.createElement("span");
    text.textContent = collection.name;
    const detail = document.createElement("small");
    detail.textContent = plural(collection.count, contentType === "shows" ? "show" : "movie");
    text.append(detail);
    label.append(box, text);
    row.append(label);
    list.append(row);
  }
  list.hidden = catalog.collections.length === 0;
  $("#collection-empty").hidden = catalog.collections.length > 0;
  $("#collection-empty").textContent =
    `No collections with ${contentType} yet. Create one in Jellyfin, include Collections under Settings → Library, then run a library scan.`;
}

async function renderContentOptions(selectedGenres = [], selectedStudios = [], selectedCollections = []) {
  const contentType = new FormData(channelForm).get("contentType");
  const catalog = await loadCatalog(contentType);
  renderChips($("#genre-options"), catalog.genres, selectedGenres);
  renderChips($("#studio-options"), catalog.studios, selectedStudios);
  renderCollectionList(catalog, contentType, selectedCollections);
  $("#studio-label").textContent = contentType === "shows" ? "Networks" : "Studios";
  $("#year-hint").textContent = catalog.minYear
    ? `Your library runs from ${catalog.minYear} to ${catalog.maxYear}. "Before" isn't included: from 1980, before 1990 means the 1980s.`
    : "";
  renderTitleList();
}

function syncEditorMode() {
  const mode = new FormData(channelForm).get("mode");
  for (const section of channelForm.querySelectorAll("[data-mode]")) {
    section.hidden = section.dataset.mode !== mode;
  }
}

function schedulePreview() {
  clearTimeout(previewTimer);
  previewTimer = setTimeout(updatePreview, 250);
}

function syncColorMode() {
  const custom = new FormData(channelForm).get("colorMode") === "custom";
  channelForm.elements.color.hidden = !custom;
  $("#color-auto-swatch").hidden = custom;
}

function showAutomaticColor(color) {
  automaticColor = color;
  $("#color-auto-swatch").style.background = color;
}

async function updatePreview() {
  const body = currentChannelBody();
  const preview = $("#channel-preview");
  if (body.mode !== "filter" && body.itemIds.length === 0) {
    preview.textContent = body.mode === "pick" ? "Choose at least one title." : "Choose at least one collection.";
    return;
  }

  const request = ++previewRequest;
  try {
    const query = editingChannel ? `?channelId=${editingChannel.id}` : "";
    const result = await api("POST", `/api/channels/preview${query}`, body);
    if (request !== previewRequest) return; // A newer preview is on its way.
    showAutomaticColor(result.automaticColor);
    if (result.count === 0) {
      preview.textContent = "Nothing in your library matches this yet.";
      return;
    }
    const size = body.contentType === "shows"
      ? `${plural(result.count, "show")}, ${plural(result.episodes, "episode")}`
      : plural(result.count, "movie");
    preview.textContent = `${size}, about ${plural(result.hours, "hour")} of programming. Includes ${result.samples.join(", ")}.`;
  } catch (err) {
    if (request === previewRequest) preview.textContent = err.message;
  }
}

async function openEditor(channelId) {
  channelForm.reset();
  airOrderChosen = false;
  logoFile = null;
  logoRemoved = false;
  setMessage(channelForm, null);
  $("#title-search").value = "";
  catalogs.clear(); // The index may have changed since the last visit.
  pickedTitles = new Set();
  editingChannel = channelId === null ? null : await api("GET", `/api/channels/${channelId}`);

  $("#editor-title").textContent = editingChannel ? `Edit ${editingChannel.name}` : "Build a channel";
  $("#channel-delete").hidden = !editingChannel;
  channelForm.elements.number.placeholder = "Auto";
  setLogoPreview(null);

  if (editingChannel) {
    const fields = channelForm.elements;
    fields.name.value = editingChannel.name;
    fields.number.value = editingChannel.number;
    fields.contentType.value = editingChannel.contentType;
    fields.mode.value = editingChannel.mode;
    fields.airOrder.value = editingChannel.airOrder;
    fields.colorMode.value = editingChannel.colorCustom ? "custom" : "auto";
    fields.color.value = editingChannel.color ?? fields.color.value;
    showAutomaticColor(editingChannel.color);
    fields.yearFrom.value = editingChannel.yearFrom ?? "";
    fields.yearBefore.value = editingChannel.yearBefore ?? "";
    if (editingChannel.mode === "pick") pickedTitles = new Set(editingChannel.itemIds);
  }

  syncEditorMode();
  syncColorMode();
  await renderContentOptions(
    editingChannel?.genres ?? [],
    editingChannel?.studios ?? [],
    editingChannel?.mode === "collection" ? editingChannel.itemIds : [],
  );
  await updatePreview();
}

channelForm.addEventListener("change", (event) => {
  const name = event.target.name;
  if (name === "contentType") {
    pickedTitles = new Set(); // Titles and categories belong to one content type.
    // A new channel starts on the usual order for its type until the admin picks one.
    if (!editingChannel && !airOrderChosen) {
      channelForm.elements.airOrder.value = event.target.value === "shows" ? "oldest" : "random";
    }
    renderContentOptions().then(schedulePreview).catch((err) => setMessage(channelForm, "error", err.message));
    return;
  }
  if (name === "airOrder") { airOrderChosen = true; return; }
  if (name === "color") return;
  if (name === "colorMode") {
    // Start from the color the channel would get anyway.
    if (event.target.value === "custom" && automaticColor && !editingChannel?.colorCustom) {
      channelForm.elements.color.value = automaticColor;
    }
    syncColorMode();
    return;
  }
  if (name === "mode") syncEditorMode();
  schedulePreview();
});

channelForm.addEventListener("input", (event) => {
  if (event.target.name === "yearFrom" || event.target.name === "yearBefore") schedulePreview();
});

$("#title-search").addEventListener("input", renderTitleList);

channelForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  setMessage(channelForm, null);
  const path = editingChannel ? `/api/channels/${editingChannel.id}` : "/api/channels";
  let saved = null;
  try {
    await busy([$("#channel-save")], async () => {
      saved = await api(editingChannel ? "PUT" : "POST", path, currentChannelBody());
      if (logoFile) await api("PUT", `/api/channels/${saved.id}/logo`, logoFile);
      else if (logoRemoved && saved.logoUrl) await api("DELETE", `/api/channels/${saved.id}/logo`);
    });
    location.hash = "#/guide";
  } catch (err) {
    if (saved) editingChannel = saved; // It exists now, so trying again mustn't create another.
    setMessage(channelForm, "error", saved ? `The channel was saved, but its logo wasn't. ${err.message}` : err.message);
  }
});

$("#channel-delete").addEventListener("click", async () => {
  if (!editingChannel || !confirm(`Delete ${editingChannel.name}? Its schedule goes with it.`)) return;
  try {
    await api("DELETE", `/api/channels/${editingChannel.id}`);
    location.hash = "#/guide";
  } catch (err) {
    setMessage(channelForm, "error", err.message);
  }
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
