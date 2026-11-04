"use strict";
(() => {
  const byId = id => document.getElementById(id);
  const canvas = byId("image"), context = canvas.getContext("2d");
  const points = [], labels = ["Top left", "Top right", "Bottom right", "Bottom left"];
  let image = null, revision = 0, loading = 0, activeRequest = null, downloadUrl = null;
  const status = (text, error = false) => { byId("status").textContent = text; byId("status").dataset.error = String(error); };
  const changed = () => {
    revision++; activeRequest?.abort(); byId("download").hidden = true; byId("download").removeAttribute("href");
    if (downloadUrl) { URL.revokeObjectURL(downloadUrl); downloadUrl = null; }
  };

  function draw() {
    byId("undo").disabled = byId("reset").disabled = points.length === 0;
    byId("export").disabled = points.length !== 4 || activeRequest !== null;
    if (!image) return;
    context.drawImage(image, 0, 0);
    const scale = Math.max(image.width, image.height) / 640;
    context.lineWidth = 2 * scale;
    context.strokeStyle = "#62fff0";
    context.fillStyle = "#07151a";
    context.beginPath();
    points.forEach((point, i) => { if (i === 0) context.moveTo(point.u, point.v); else context.lineTo(point.u, point.v); });
    if (points.length === 4) context.closePath();
    context.stroke();
    points.forEach((point, i) => {
      context.beginPath(); context.arc(point.u, point.v, 11 * scale, 0, 2 * Math.PI); context.fill(); context.stroke();
      context.font = `bold ${13 * scale}px system-ui`; context.textAlign = "center"; context.textBaseline = "middle";
      context.fillStyle = "#ffffff"; context.fillText(String(i + 1), point.u, point.v); context.fillStyle = "#07151a";
    });
  }

  function pointList() {
    byId("points").replaceChildren();
    points.forEach((point, i) => {
      const row = document.createElement("li"), fields = document.createElement("div");
      row.append(document.createTextNode(labels[i]), fields);
      for (const axis of ["u", "v"]) {
        const label = document.createElement("label"), input = document.createElement("input");
        label.textContent = axis.toUpperCase(); input.type = "number"; input.step = "0.1"; input.min = "0"; input.required = true;
        input.max = String((axis === "u" ? image.width : image.height) - 1); input.value = String(point[axis]);
        input.setAttribute("aria-label", `Point ${i + 1}, ${axis}`);
        input.addEventListener("input", () => { changed(); draw(); status("Coordinates changed. Commit a valid value before validating the profile."); });
        input.addEventListener("change", () => {
          if (!input.checkValidity() || !Number.isFinite(input.valueAsNumber)) { input.reportValidity(); return; }
          point[axis] = input.valueAsNumber; changed(); draw(); status("Point updated. Validate the geometry before downloading.");
        });
        label.append(input); fields.append(label);
      }
      byId("points").append(row);
    });
    draw();
  }

  // Read dimensions before decode, and reject EXIF to keep pixel orientation unambiguous.
  function jpegSize(buffer) {
    const data = new Uint8Array(buffer), view = new DataView(buffer);
    if (data.length < 4 || data[0] !== 255 || data[1] !== 216) throw new Error("Choose a valid JPEG created by the snapshot command.");
    let offset = 2, size = null;
    while (offset < data.length) {
      if (data[offset++] !== 255) break;
      while (data[offset] === 255) offset++;
      const marker = data[offset++];
      if (marker === 218 || marker === 217) break;
      if (marker === 1 || (marker >= 208 && marker <= 215)) continue;
      if (offset + 2 > data.length) break;
      const length = view.getUint16(offset);
      if (length < 2 || offset + length > data.length) break;
      if (marker === 225 && length >= 8 && data.subarray(offset + 2, offset + 8).every((x, i) => x === [69,120,105,102,0,0][i]))
        throw new Error("This image contains EXIF metadata. Capture a frame with the snapshot command to keep pixel coordinates aligned.");
      if ([192,193,194].includes(marker) && length >= 8) {
        const height = view.getUint16(offset + 3), width = view.getUint16(offset + 5);
        if (data[offset + 2] !== 8 || width < 2 || height < 2 || width * height > 4194304)
          throw new Error("The JPEG must use 8 bits per channel and contain no more than 4,194,304 pixels.");
        size = {width, height};
      }
      offset += length;
    }
    if (!size) throw new Error("Could not read the JPEG dimensions.");
    return size;
  }

  byId("file").addEventListener("change", async event => {
    const ticket = ++loading, file = event.target.files[0];
    changed(); image = null; points.length = 0; canvas.hidden = true; byId("empty").hidden = false;
    byId("dimensions").textContent = "No image loaded"; pointList();
    if (!file) { status("Open an image to begin."); return; }
    let url = null;
    try {
      if (file.size > 8388608) throw new Error("The JPEG exceeds the 8 MiB limit.");
      const size = jpegSize(await file.arrayBuffer());
      url = URL.createObjectURL(file);
      const decoded = new Image(); decoded.src = url; await decoded.decode();
      if (ticket !== loading) return;
      if (decoded.naturalWidth !== size.width || decoded.naturalHeight !== size.height) throw new Error("Decoded dimensions do not match the JPEG header.");
      image = decoded; canvas.width = size.width; canvas.height = size.height;
      byId("dimensions").textContent = `${size.width} × ${size.height} px`;
      canvas.hidden = false; byId("empty").hidden = true; draw(); status("Select point 1: the top-left workspace corner.");
    } catch (error) { if (ticket === loading) status(error.message, true); }
    finally { if (url) URL.revokeObjectURL(url); }
  });

  canvas.addEventListener("click", event => {
    if (!image || points.length === 4) return;
    const box = canvas.getBoundingClientRect();
    points.push({u: Math.round(Math.max(0, Math.min(image.width - 1, (event.clientX - box.left) / box.width * image.width)) * 10) / 10,
      v: Math.round(Math.max(0, Math.min(image.height - 1, (event.clientY - box.top) / box.height * image.height)) * 10) / 10});
    changed(); pointList(); status(points.length === 4 ? "Four points selected. Validate the profile, then download it." : `Select point ${points.length + 1}: ${labels[points.length].toLowerCase()}.`);
  });
  byId("undo").addEventListener("click", () => { points.pop(); changed(); pointList(); status("Last point removed."); });
  byId("reset").addEventListener("click", () => { points.length = 0; changed(); pointList(); status("Select the four corners again."); });
  for (const id of ["width", "depth"]) byId(id).addEventListener("input", () => { changed(); status("Scale changed. Validate the profile again."); });

  async function boundedText(response) {
    if (!response.body) throw new Error("The server returned an empty response.");
    const reader = response.body.getReader(), chunks = []; let length = 0;
    try {
      while (true) {
        const {value, done} = await reader.read(); if (done) break;
        length += value.length; if (length > 16384) throw new Error("The response exceeds the profile size limit."); chunks.push(value);
      }
    } finally { await reader.cancel(); reader.releaseLock(); }
    const bytes = new Uint8Array(length); let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
    return new TextDecoder("utf-8", {fatal: true}).decode(bytes);
  }

  async function validate(profile, signal) {
    const end = performance.now() + 15000;
    for (let attempt = 0; attempt < 3; attempt++) {
      const remaining = end - performance.now(); if (remaining <= 0) break;
      const timeout = AbortSignal.timeout(Math.min(4000, Math.floor(remaining)));
      let delay = 250 * 2 ** attempt;
      try {
        const response = await fetch("/api/calibration/validate", {method: "POST", credentials: "same-origin", cache: "no-store",
          headers: {"Content-Type": "application/json", "X-OmniTwin-Calibration": "1"}, body: JSON.stringify(profile), signal: AbortSignal.any([signal, timeout])});
        if (response.ok) {
          if (!(response.headers.get("content-type") || "").startsWith("application/json")) throw new Error("The server returned an invalid response format.");
          const text = await boundedText(response);
          if (JSON.stringify(JSON.parse(text)) !== JSON.stringify(profile)) throw new Error("The server response does not match the submitted profile.");
          return text;
        }
        if (response.status === 400) throw new Error("The geometry was rejected. Check the order, spacing, and placement of the four corners.");
        if (![408,429,500,502,503,504].includes(response.status)) throw new Error(`Validation rejected (HTTP ${response.status}). Refresh the page from the local backend.`);
        const retry = response.headers.get("retry-after");
        if (retry) { const seconds = Number(retry); const wait = Number.isFinite(seconds) ? seconds * 1000 : Date.parse(retry) - Date.now(); if (Number.isFinite(wait)) delay = Math.max(delay, wait); }
        await response.body?.cancel();
      } catch (error) {
        if (signal.aborted) throw signal.reason;
        if (!(error instanceof TypeError) && error.name !== "TimeoutError" && error.name !== "AbortError") throw error;
      }
      if (attempt === 2 || delay >= end - performance.now()) break;
      await new Promise((resolve, reject) => {
        const abort = () => { clearTimeout(timer); reject(signal.reason); };
        const timer = setTimeout(() => { signal.removeEventListener("abort", abort); resolve(); }, delay);
        signal.addEventListener("abort", abort, {once: true}); if (signal.aborted) abort();
      });
    }
    throw new Error("The backend did not respond within three attempts. Check that it is still running, then try again.");
  }

  byId("export").addEventListener("click", async () => {
    if (!image || points.length !== 4 || activeRequest) return;
    if ([...byId("points").querySelectorAll("input"), byId("width"), byId("depth")].some(input => !input.reportValidity())) return;
    const width = byId("width").valueAsNumber, depth = byId("depth").valueAsNumber;
    if (![width, depth].every(x => Number.isFinite(x) && x >= .01 && x <= 10000)) return;
    const profile = {schemaVersion: 1, calibrationId: crypto.randomUUID(), imageWidth: image.width, imageHeight: image.height,
      cameraCorners: points.map(point => ({...point})), worldCorners: [{x: -width/2, z: -depth/2}, {x: width/2, z: -depth/2}, {x: width/2, z: depth/2}, {x: -width/2, z: depth/2}],
      minimumRadiusMeters: .05, maximumRadiusMeters: 3, safetyMarginMeters: .1, minimumConfidence: .3};
    const ticket = revision, controller = new AbortController(); activeRequest = controller; draw(); status("Validating the schema and geometry…");
    try {
      const text = await validate(profile, controller.signal);
      if (ticket !== revision) return;
      if (downloadUrl) URL.revokeObjectURL(downloadUrl);
      downloadUrl = URL.createObjectURL(new Blob([text], {type: "application/json"}));
      byId("download").href = downloadUrl; byId("download").hidden = false;
      status("Profile validated. Download the file to local/ and check its accuracy against the tabletop.");
    } catch (error) { if (!controller.signal.aborted) status(error.message, true); }
    finally { activeRequest = null; draw(); }
  });
})();
