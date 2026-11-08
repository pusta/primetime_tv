"use strict";

// Small management UI: first-run setup, sign-in, and the Jellyfin connection.

const $ = (selector, root = document) => root.querySelector(selector);

const views = ["setup-view", "login-view", "dashboard-view"];

function show(viewId) {
  $("#loading").hidden = true;
  for (const id of views) $("#" + id).hidden = id !== viewId;
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
  if (response.status === 401 && path.startsWith("/api/jellyfin")) {
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

// ---- Routing --------------------------------------------------------------

async function route() {
  const session = await api("GET", "/api/session");
  $("#userbar").hidden = !session.authenticated;
  $("#current-user").textContent = session.username ?? "";

  if (session.needsSetup) return show("setup-view");
  if (!session.authenticated) return show("login-view");
  show("dashboard-view");
  await loadDashboard();
}

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

function renderLibraries(libraries, note) {
  const body = $("#libraries-body");
  body.replaceChildren();
  for (const lib of libraries) {
    const row = document.createElement("tr");
    const name = document.createElement("td");
    name.textContent = lib.name;
    const type = document.createElement("td");
    type.textContent = lib.collectionType;
    const paths = document.createElement("td");
    paths.className = "paths";
    paths.textContent = lib.locations.length ? lib.locations.join(", ") : "—";
    row.append(name, type, paths);
    body.append(row);
  }
  $("#libraries-empty").hidden = libraries.length > 0;
  $("#libraries-note").textContent = note;
  $("#libraries-card").hidden = false;
}

function renderStatus(status) {
  const badge = $("#conn-badge");
  const connected = status.connected;
  badge.textContent = !status.configured ? "Not connected" : connected ? "Connected" : "Unreachable";
  badge.classList.toggle("ok", connected);

  $("#conn-summary").hidden = !status.configured;
  $("#disconnect-btn").hidden = !status.configured;
  if (!status.configured) {
    $("#libraries-card").hidden = true;
    return;
  }

  $("#conn-name").textContent = status.server?.name ?? savedConnection?.serverName ?? "—";
  $("#conn-version").textContent = status.server?.version ?? savedConnection?.serverVersion ?? "—";
  $("#conn-url").textContent = savedConnection?.url ?? "";
  $("#conn-auth").textContent = savedConnection?.authMethod === "password"
    ? `Jellyfin user “${savedConnection.username}”`
    : "API key";

  const error = $("#conn-error");
  error.hidden = connected;
  error.textContent = connected ? "" : status.error ?? "";

  if (connected) {
    renderLibraries(status.libraries, `${status.libraries.length} found`);
  } else {
    $("#libraries-card").hidden = true;
  }
}

async function loadDashboard() {
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

const jfButtons = () => [$("#test-btn"), $("#save-btn"), $("#disconnect-btn")];

$("#test-btn").addEventListener("click", async () => {
  if (!jfForm.reportValidity()) return;
  setMessage(jfForm, null);
  try {
    const result = await busy(jfButtons(), () => api("POST", "/api/jellyfin/test", formPayload()));
    setMessage(jfForm, "success",
      `Connected to ${result.server.name} (Jellyfin ${result.server.version}). Not saved yet.`);
    renderLibraries(result.libraries, `${result.libraries.length} found (test, not saved)`);
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
    await loadDashboard();
    setMessage(jfForm, "success", `Saved. Connected to ${result.server.name}.`);
  } catch (err) {
    setMessage(jfForm, "error", err.message);
  }
});

$("#disconnect-btn").addEventListener("click", async () => {
  if (!confirm("Remove the saved Jellyfin connection?")) return;
  try {
    await busy(jfButtons(), () => api("DELETE", "/api/jellyfin"));
    jfForm.reset();
    await loadDashboard();
  } catch (err) {
    setMessage(jfForm, "error", err.message);
  }
});

route().catch((err) => {
  $("#loading").textContent = `Couldn't reach the PrimeTime server: ${err.message}`;
});
