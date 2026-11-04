"use strict";
(() => {
  const byId = id => document.getElementById(id);
  const canvas = byId("image"), context = canvas.getContext("2d");
  const viewport = byId("image-viewport");
  const points = [], boundary = [], outlineHistory = [], labels = ["Top left", "Top right", "Bottom right", "Bottom left"];
  let addOutlinePoint = false;
  let image = null, revision = 0, loading = 0, activeRequest = null, downloadUrl = null, zoom = 1, fitScale = 1;
  const status = (text, error = false) => { byId("status").textContent = text; byId("status").dataset.error = String(error); };
  const changed = () => {
    revision++; activeRequest?.abort(); byId("download").hidden = true; byId("download").removeAttribute("href");
    if (downloadUrl) { URL.revokeObjectURL(downloadUrl); downloadUrl = null; }
  };

  function draw() {
    byId("undo").disabled = byId("reset").disabled = points.length === 0;
    byId("outline-toggle").disabled = points.length !== 4;
    byId("outline-toggle").textContent = addOutlinePoint ? "Finish outline" : "Add outline point";
    byId("outline-toggle").classList.toggle("selected", addOutlinePoint);
    byId("outline-undo").disabled = outlineHistory.length === 0;
    byId("outline-count").textContent = boundary.length > 4
      ? `${boundary.length} outline vertices (${boundary.length - 4} added).`
      : "Using the four corner points as the outline.";
    byId("export").disabled = points.length !== 4 || activeRequest !== null;
    if (!image) return;
    context.drawImage(image, 0, 0);
    const scale = Math.max(image.width, image.height) / 640;
    context.lineWidth = 2 * scale;
    context.strokeStyle = "#62fff0";
    context.fillStyle = "#07151a";
    context.beginPath();
    const outline = points.length === 4 ? boundary : points;
    outline.forEach((point, i) => { if (i === 0) context.moveTo(point.u, point.v); else context.lineTo(point.u, point.v); });
    if (points.length === 4) context.closePath();
    context.stroke();
    outline.forEach((point, i) => {
      const isExtra = points.length === 4 && point.anchorIndex === null;
      context.beginPath(); context.arc(point.u, point.v, (isExtra ? 7 : 11) * scale, 0, 2 * Math.PI);
      context.fillStyle = isExtra ? "#ffcf70" : "#07151a"; context.fill();
      context.strokeStyle = isExtra ? "#ffcf70" : "#62fff0"; context.stroke();
      context.font = `bold ${12 * scale}px system-ui`; context.textAlign = "center"; context.textBaseline = "middle";
      context.fillStyle = isExtra ? "#07151a" : "#ffffff";
      context.fillText(isExtra ? "+" : String((point.anchorIndex ?? i) + 1), point.u, point.v);
    });
    context.strokeStyle = "#62fff0";
    canvas.style.cursor = addOutlinePoint ? "copy" : "crosshair";
  }

  function fitImage() {
    if (!image) return;
    const maxHeight = Math.min(window.innerHeight * 0.65, 760);
    fitScale = Math.min(1, viewport.clientWidth / image.width, maxHeight / image.height);
    applyZoom();
  }

  function applyZoom(keepCenter = false) {
    if (!image) return;
    const oldWidth = Number.parseFloat(canvas.style.width) || image.width * fitScale * zoom;
    const oldHeight = Number.parseFloat(canvas.style.height) || image.height * fitScale * zoom;
    const centerX = viewport.scrollLeft + viewport.clientWidth / 2;
    const centerY = viewport.scrollTop + viewport.clientHeight / 2;
    const width = Math.max(1, Math.round(image.width * fitScale * zoom));
    const height = Math.max(1, Math.round(image.height * fitScale * zoom));
    canvas.style.width = `${width}px`;
    canvas.style.height = `${height}px`;
    byId("zoom-level").textContent = `${Math.round(zoom * 100)}% of fit`;
    byId("zoom-out").disabled = zoom <= 0.5;
    byId("zoom-in").disabled = zoom >= 4;
    if (keepCenter && oldWidth > 0 && oldHeight > 0) {
      viewport.scrollLeft = Math.max(0, centerX * width / oldWidth - viewport.clientWidth / 2);
      viewport.scrollTop = Math.max(0, centerY * height / oldHeight - viewport.clientHeight / 2);
    }
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
          point[axis] = input.valueAsNumber;
          const boundaryAnchor = boundary.find(candidate => candidate.anchorIndex === i);
          if (boundaryAnchor) boundaryAnchor[axis] = point[axis];
          changed(); draw(); status("Point updated. Validate the geometry before downloading.");
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
    changed(); image = null; points.length = 0; viewport.hidden = true; byId("image-tools").hidden = true; byId("empty").hidden = false;
    byId("dimensions").textContent = "No image loaded"; boundary.length = 0; outlineHistory.length = 0; addOutlinePoint = false; pointList();
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
      viewport.hidden = false; byId("image-tools").hidden = false; byId("empty").hidden = true;
      zoom = 1; viewport.scrollLeft = viewport.scrollTop = 0; fitImage(); draw();
      status("Zoom in if needed. Select point 1 at the top-left corner of the tabletop surface.");
    } catch (error) { if (ticket === loading) status(error.message, true); }
    finally { if (url) URL.revokeObjectURL(url); }
  });

  canvas.addEventListener("click", event => {
    if (!image) return;
    const box = canvas.getBoundingClientRect();
    if (box.width <= 0 || box.height <= 0) return;
    const clicked = {u: Math.round(Math.max(0, Math.min(image.width - 1, (event.clientX - box.left) / box.width * image.width)) * 10) / 10,
      v: Math.round(Math.max(0, Math.min(image.height - 1, (event.clientY - box.top) / box.height * image.height)) * 10) / 10};
    if (points.length === 4) {
      if (!addOutlinePoint) return;
      if (boundary.length >= 64) { status("The outline limit is 64 vertices. Finish the outline or undo a point.", true); return; }
      if (boundary.some(point => point.u === clicked.u && point.v === clicked.v)) {
        status("That pixel is already a boundary point. Click a different spot along the edge.", true); return;
      }
      let nearestIndex = 0, nearestDistance = Infinity;
      for (let i = 0; i < boundary.length; i++) {
        const start = boundary[i], end = boundary[(i + 1) % boundary.length];
        const dx = end.u - start.u, dy = end.v - start.v, lengthSquared = dx * dx + dy * dy;
        const t = lengthSquared === 0 ? 0 : Math.max(0, Math.min(1, ((clicked.u - start.u) * dx + (clicked.v - start.v) * dy) / lengthSquared));
        const offsetU = clicked.u - (start.u + t * dx), offsetV = clicked.v - (start.v + t * dy);
        const distance = offsetU * offsetU + offsetV * offsetV;
        if (distance < nearestDistance) { nearestIndex = i; nearestDistance = distance; }
      }
      const inserted = {...clicked, anchorIndex: null};
      boundary.splice(nearestIndex + 1, 0, inserted); outlineHistory.push(inserted);
      changed(); draw(); status("Outline point added between the nearest boundary vertices. Add more points or finish the outline.");
      return;
    }
    points.push(clicked);
    if (points.length === 4) {
      boundary.splice(0, boundary.length, ...points.map((point, anchorIndex) => ({...point, anchorIndex})));
      outlineHistory.length = 0; addOutlinePoint = false;
    }
    changed(); pointList(); status(points.length === 4 ? "Four points selected. Validate the profile, then download it." : `Select point ${points.length + 1}: ${labels[points.length].toLowerCase()}.`);
  });
  byId("undo").addEventListener("click", () => { points.pop(); boundary.length = 0; outlineHistory.length = 0; addOutlinePoint = false; changed(); pointList(); status("Last corner removed. Select the four corners again."); });
  byId("reset").addEventListener("click", () => { points.length = 0; boundary.length = 0; outlineHistory.length = 0; addOutlinePoint = false; changed(); pointList(); status("Select the four corners again."); });
  byId("outline-toggle").addEventListener("click", () => {
    if (points.length !== 4) return;
    addOutlinePoint = !addOutlinePoint; draw();
    status(addOutlinePoint ? "Outline editing is on. Click near an edge to insert a point on its nearest segment." : "Outline editing is finished. Validate the profile to download it.");
  });
  byId("outline-undo").addEventListener("click", () => {
    const last = outlineHistory.pop();
    if (!last) return;
    const index = boundary.indexOf(last); if (index >= 0) boundary.splice(index, 1);
    changed(); draw(); status("Last added outline point removed.");
  });
  byId("zoom-out").addEventListener("click", () => { zoom = Math.max(0.5, zoom / 1.25); applyZoom(true); });
  byId("zoom-in").addEventListener("click", () => { zoom = Math.min(4, zoom * 1.25); applyZoom(true); });
  byId("zoom-fit").addEventListener("click", () => { zoom = 1; viewport.scrollLeft = viewport.scrollTop = 0; fitImage(); });
  window.addEventListener("resize", () => { if (image) fitImage(); });
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
        if (response.status === 400) throw new Error("The geometry was rejected. Check the order and placement of the four corners and the tabletop outline.");
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
      workspaceBoundary: boundary.map(({u, v}) => ({u, v})),
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
