// W3 Studio 3D World Map Engine (Powered by GhostWolf's War3MapViewer & WebGL)
(function () {
  'use strict';

  const MV = window.ModelViewer;
  if (!MV || !MV.viewer || !MV.viewer.handlers || !MV.viewer.handlers.War3MapViewer) {
    console.error('War3MapViewer handler is not available.');
    return;
  }

  const canvas = document.getElementById('map-canvas');
  const loadingOverlay = document.getElementById('loading-overlay');
  const loadingDetails = document.getElementById('loading-details');
  const loadingSpinner = document.getElementById('loading-spinner');
  const noMapHint = document.getElementById('no-map-hint');

  function pathSolver(src, params) {
    if (!src) return '';
    let clean = src.replace(/\\/g, '/').replace(/^\/+/, '');
    let url = 'https://w3studio.local/mpq/' + clean;
    if (params && params.tileset) {
      url += '?tileset=' + encodeURIComponent(params.tileset);
    }
    return url;
  }

  // Create the full 3D Map Viewer instance
  const War3MapViewer = MV.viewer.handlers.War3MapViewer;
  let viewer = null;
  try {
    viewer = new War3MapViewer(canvas, pathSolver, false);
  } catch (err) {
    console.error('Failed to initialize War3MapViewer:', err);
    if (loadingOverlay) loadingOverlay.style.display = 'block';
    if (loadingDetails) loadingDetails.textContent = 'Error WebGL: ' + err.message;
    return;
  }

  const cameraState = {
    horizontalAngle: 0,
    verticalAngle: Math.PI / 4,
    distance: 3200,
    target: [0, 0, 0],
    isRotating: false,
    isPanning: false,
    isPainting: false,
    downX: 0,
    downY: 0,
    lastX: 0,
    lastY: 0,
    rightDownX: 0,
    rightDownY: 0
  };

  const brushState = {
    active: false,
    size: 2,
    isSquare: false,
    hoverWx: null,
    hoverWy: null,
    lastDabTime: 0,
    lastDabWx: 0,
    lastDabWy: 0,
    pendingDab: null,
    dabRafId: 0,
    inFlightCount: 0,
    reloadQueue: Promise.resolve()
  };

  const editorState = {
    mode: 'select', // 'select' | 'terrain' | 'place_unit' | 'place_doodad'
    placeSpec: null, // { kind, rawcode, player, angle, scale, variation, life, modelPath }
    selectedEntity: null, // { kind, creationNumber, id, x, y, z, angle, scale, player, variation, life }
    isDraggingEntity: false,
    dragMoved: false,
    hoverWx: null,
    hoverWy: null
  };

  // Overlay 2D canvas for terrain brush & selection rings (pointer-events: none)
  let overlayCanvas = document.getElementById('brush-overlay');
  if (!overlayCanvas && canvas && canvas.parentElement) {
    overlayCanvas = document.createElement('canvas');
    overlayCanvas.id = 'brush-overlay';
    overlayCanvas.style.position = 'absolute';
    overlayCanvas.style.inset = '0';
    overlayCanvas.style.width = '100%';
    overlayCanvas.style.height = '100%';
    overlayCanvas.style.pointerEvents = 'none';
    overlayCanvas.style.zIndex = '5';
    canvas.parentElement.appendChild(overlayCanvas);
  }
  const overlayCtx = overlayCanvas ? overlayCanvas.getContext('2d') : null;

  function postMsg(obj) {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(obj);
    }
  }

  function updateCamera() {
    if (!viewer || !viewer.map || !viewer.map.worldScene) return;

    let scene = viewer.map.worldScene;
    let phi = Math.min(Math.max(0.05, cameraState.verticalAngle), Math.PI / 2 - 0.04);
    cameraState.verticalAngle = phi;
    let theta = cameraState.horizontalAngle;
    let d = cameraState.distance;

    let x = d * Math.sin(phi) * Math.sin(theta) + cameraState.target[0];
    let y = -d * Math.sin(phi) * Math.cos(theta) + cameraState.target[1];
    let z = d * Math.cos(phi) + cameraState.target[2];

    scene.camera.moveToAndFace([x, y, z], cameraState.target, [0, 0, 1]);
  }

  function resizeCanvas() {
    let w = Math.max(canvas.parentElement.clientWidth || 400, 50);
    let h = Math.max(canvas.parentElement.clientHeight || 400, 50);
    if (canvas.width !== w || canvas.height !== h) {
      canvas.width = w;
      canvas.height = h;
    }
    if (overlayCanvas && (overlayCanvas.width !== w || overlayCanvas.height !== h)) {
      overlayCanvas.width = w;
      overlayCanvas.height = h;
    }
    if (viewer && viewer.map && viewer.map.worldScene) {
      viewer.map.worldScene.viewport = [0, 0, w, h];
      viewer.map.worldScene.camera.perspective(Math.PI / 4, w / h, 100, 35000);
    }
  }

  window.addEventListener('resize', resizeCanvas);
  resizeCanvas();

  function invertMat4(m) {
    let out = new Float32Array(16);
    let a00 = m[0], a01 = m[1], a02 = m[2], a03 = m[3];
    let a10 = m[4], a11 = m[5], a12 = m[6], a13 = m[7];
    let a20 = m[8], a21 = m[9], a22 = m[10], a23 = m[11];
    let a30 = m[12], a31 = m[13], a32 = m[14], a33 = m[15];

    let b00 = a00 * a11 - a01 * a10;
    let b01 = a00 * a12 - a02 * a10;
    let b02 = a00 * a13 - a03 * a10;
    let b03 = a01 * a12 - a02 * a11;
    let b04 = a01 * a13 - a03 * a11;
    let b05 = a02 * a13 - a03 * a12;
    let b06 = a20 * a31 - a21 * a30;
    let b07 = a20 * a32 - a22 * a30;
    let b08 = a20 * a33 - a23 * a30;
    let b09 = a21 * a32 - a22 * a31;
    let b10 = a21 * a33 - a23 * a31;
    let b11 = a22 * a33 - a23 * a32;

    let det = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06;
    if (!det) return null;
    det = 1.0 / det;

    out[0] = (a11 * b11 - a12 * b10 + a13 * b09) * det;
    out[1] = (a02 * b10 - a01 * b11 - a03 * b09) * det;
    out[2] = (a31 * b05 - a32 * b04 + a33 * b03) * det;
    out[3] = (a22 * b04 - a21 * b05 - a23 * b03) * det;
    out[4] = (a12 * b08 - a10 * b11 - a13 * b07) * det;
    out[5] = (a00 * b11 - a02 * b08 + a03 * b07) * det;
    out[6] = (a32 * b02 - a30 * b05 - a33 * b01) * det;
    out[7] = (a20 * b05 - a22 * b02 + a23 * b01) * det;
    out[8] = (a10 * b10 - a11 * b08 + a13 * b06) * det;
    out[9] = (a01 * b08 - a00 * b10 - a03 * b06) * det;
    out[10] = (a30 * b04 - a31 * b02 + a33 * b00) * det;
    out[11] = (a21 * b02 - a20 * b04 - a23 * b00) * det;
    out[12] = (a11 * b07 - a10 * b09 - a12 * b06) * det;
    out[13] = (a00 * b09 - a01 * b07 + a02 * b06) * det;
    out[14] = (a31 * b01 - a30 * b03 - a32 * b00) * det;
    out[15] = (a20 * b03 - a21 * b01 + a22 * b00) * det;
    return out;
  }

  function unprojectNdc(ndcX, ndcY, ndcZ, invVP) {
    let x = ndcX * invVP[0] + ndcY * invVP[4] + ndcZ * invVP[8] + invVP[12];
    let y = ndcX * invVP[1] + ndcY * invVP[5] + ndcZ * invVP[9] + invVP[13];
    let z = ndcX * invVP[2] + ndcY * invVP[6] + ndcZ * invVP[10] + invVP[14];
    let w = ndcX * invVP[3] + ndcY * invVP[7] + ndcZ * invVP[11] + invVP[15];
    if (Math.abs(w) < 1e-8) return null;
    return [x / w, y / w, z / w];
  }

  function projectWorld(wx, wy, wz, vp) {
    let x = wx * vp[0] + wy * vp[4] + wz * vp[8] + vp[12];
    let y = wx * vp[1] + wy * vp[5] + wz * vp[9] + vp[13];
    let w = wx * vp[3] + wy * vp[7] + wz * vp[11] + vp[15];
    if (w <= 0.001) return null;
    let ndcX = x / w;
    let ndcY = y / w;
    return [
      (ndcX + 1) * 0.5 * overlayCanvas.width,
      (1 - ndcY) * 0.5 * overlayCanvas.height
    ];
  }

  function raycastRay(clientX, clientY) {
    if (!viewer || !viewer.map || !viewer.map.worldScene) return null;
    let cam = viewer.map.worldScene.camera;
    if (!cam || !cam.viewProjectionMatrix) return null;

    let invVP = invertMat4(cam.viewProjectionMatrix);
    if (!invVP) return null;

    let rect = canvas.getBoundingClientRect();
    let w = Math.max(rect.width, 1);
    let h = Math.max(rect.height, 1);
    let ndcX = ((clientX - rect.left) / w) * 2 - 1;
    let ndcY = 1 - ((clientY - rect.top) / h) * 2;

    let p0 = unprojectNdc(ndcX, ndcY, -1, invVP);
    let p1 = unprojectNdc(ndcX, ndcY, 1, invVP);
    if (!p0 || !p1) return null;
    return [p0, p1];
  }

  function raycastTerrain(clientX, clientY) {
    let ray = raycastRay(clientX, clientY);
    if (!ray) return null;
    let p0 = ray[0], p1 = ray[1];

    let dx = p1[0] - p0[0];
    let dy = p1[1] - p0[1];
    let dz = p1[2] - p0[2];
    let len = Math.hypot(dx, dy, dz);
    if (len < 1e-6) return null;
    dx /= len; dy /= len; dz /= len;

    let hasHeight = typeof viewer.map.heightAt === 'function';
    let step = 64;
    let maxDist = Math.max(cameraState.distance * 4, 16000);
    let prevT = 20;

    for (let t = 20; t <= maxDist; t += step) {
      let px = p0[0] + dx * t;
      let py = p0[1] + dy * t;
      let pz = p0[2] + dz * t;
      let th = hasHeight ? viewer.map.heightAt(px, py) : 0;
      if (pz <= th) {
        let lo = prevT;
        let hi = t;
        for (let i = 0; i < 12; i++) {
          let mid = 0.5 * (lo + hi);
          let mx = p0[0] + dx * mid;
          let my = p0[1] + dy * mid;
          let mz = p0[2] + dz * mid;
          let mh = hasHeight ? viewer.map.heightAt(mx, my) : 0;
          if (mz <= mh) hi = mid;
          else lo = mid;
        }
        let ft = 0.5 * (lo + hi);
        let hx = p0[0] + dx * ft;
        let hy = p0[1] + dy * ft;
        let hz = hasHeight ? viewer.map.heightAt(hx, hy) : 0;
        return [hx, hy, hz];
      }
      prevT = t;
    }

    if (Math.abs(dz) > 1e-4) {
      let tPlane = ((cameraState.target[2] || 0) - p0[2]) / dz;
      if (tPlane > 0) {
        let hx = p0[0] + dx * tPlane;
        let hy = p0[1] + dy * tPlane;
        let hz = hasHeight ? viewer.map.heightAt(hx, hy) : (cameraState.target[2] || 0);
        return [hx, hy, hz];
      }
    }
    return null;
  }

  function getBrushWorldRadius() {
    let cells = 1.55;
    if (brushState.size <= 1) cells = 0.75;
    else if (brushState.size === 2) cells = 1.55;
    else if (brushState.size === 3) cells = 2.55;
    else if (brushState.size <= 5) cells = 4.05;
    else cells = Math.max(5.5, brushState.size * 0.8);
    return cells * 128;
  }

  function drawWorldRing(wx0, wy0, wzBase, radius, color, lineWidth, facingAngle, vp) {
    let hasHeight = typeof viewer.map.heightAt === 'function';
    overlayCtx.save();
    overlayCtx.strokeStyle = color;
    overlayCtx.lineWidth = lineWidth;
    overlayCtx.beginPath();

    let segs = 28;
    let started = false;
    for (let i = 0; i <= segs; i++) {
      let a = (i / segs) * Math.PI * 2;
      let px = wx0 + Math.cos(a) * radius;
      let py = wy0 + Math.sin(a) * radius;
      let pz = (hasHeight ? viewer.map.heightAt(px, py) : wzBase) + 8;
      let sp = projectWorld(px, py, pz, vp);
      if (!sp) continue;
      if (!started) {
        overlayCtx.moveTo(sp[0], sp[1]);
        started = true;
      } else {
        overlayCtx.lineTo(sp[0], sp[1]);
      }
    }
    if (started) overlayCtx.stroke();

    if (typeof facingAngle === 'number') {
      let c0 = projectWorld(wx0, wy0, wzBase + 10, vp);
      let tx = wx0 + Math.cos(facingAngle) * (radius * 1.45);
      let ty = wy0 + Math.sin(facingAngle) * (radius * 1.45);
      let tz = (hasHeight ? viewer.map.heightAt(tx, ty) : wzBase) + 10;
      let c1 = projectWorld(tx, ty, tz, vp);
      if (c0 && c1) {
        overlayCtx.strokeStyle = '#FFE044';
        overlayCtx.lineWidth = 2.5;
        overlayCtx.beginPath();
        overlayCtx.moveTo(c0[0], c0[1]);
        overlayCtx.lineTo(c1[0], c1[1]);
        overlayCtx.stroke();

        overlayCtx.fillStyle = '#FFE044';
        overlayCtx.beginPath();
        overlayCtx.arc(c1[0], c1[1], 4, 0, Math.PI * 2);
        overlayCtx.fill();
      }
    }
    overlayCtx.restore();
  }

  function drawViewportOverlays() {
    if (!overlayCtx || !overlayCanvas) return;
    overlayCtx.clearRect(0, 0, overlayCanvas.width, overlayCanvas.height);

    if (!viewer || !viewer.map || !viewer.map.worldScene || !viewer.map.worldScene.camera) return;
    let vp = viewer.map.worldScene.camera.viewProjectionMatrix;
    let hasHeight = typeof viewer.map.heightAt === 'function';

    // 1. Selected Entity selection circle + facing vector
    if (editorState.selectedEntity) {
      let live = typeof viewer.map.getEntityTransform === 'function'
        ? viewer.map.getEntityTransform(editorState.selectedEntity.kind, editorState.selectedEntity.creationNumber)
        : null;
      let ent = live || editorState.selectedEntity;
      let rad = Math.max(52, 68 * (ent.scale || 1));
      let ringColor = ent.kind === 'unit' ? '#33FF55' : '#40D8FF';
      drawWorldRing(ent.x, ent.y, ent.z || 0, rad, ringColor, 2.5, ent.angle, vp);
    }

    // 2. Placement ghost circle
    if ((editorState.mode === 'place_unit' || editorState.mode === 'place_doodad') &&
        editorState.hoverWx !== null && editorState.hoverWy !== null) {
      let hz = hasHeight ? viewer.map.heightAt(editorState.hoverWx, editorState.hoverWy) : 0;
      let pAngle = editorState.placeSpec ? editorState.placeSpec.angle : (270 * Math.PI) / 180;
      let pScale = editorState.placeSpec ? (editorState.placeSpec.scale || 1) : 1;
      drawWorldRing(editorState.hoverWx, editorState.hoverWy, hz, 64 * pScale, '#00FFE5', 2, pAngle, vp);
    }

    // 3. Terrain Brush circle / square
    if (!brushState.active || brushState.hoverWx === null || brushState.hoverWy === null) return;

    let r = getBrushWorldRadius();
    let wx0 = brushState.hoverWx;
    let wy0 = brushState.hoverWy;

    overlayCtx.save();
    overlayCtx.strokeStyle = cameraState.isPainting ? '#44FF66' : '#00E54C';
    overlayCtx.lineWidth = 2;
    overlayCtx.beginPath();

    let pts = [];
    if (brushState.isSquare) {
      let corners = [
        [wx0 - r, wy0 - r],
        [wx0 + r, wy0 - r],
        [wx0 + r, wy0 + r],
        [wx0 - r, wy0 + r]
      ];
      let sub = 6;
      for (let e = 0; e < 4; e++) {
        let cA = corners[e];
        let cB = corners[(e + 1) % 4];
        for (let s = 0; s < sub; s++) {
          let t = s / sub;
          pts.push([cA[0] + (cB[0] - cA[0]) * t, cA[1] + (cB[1] - cA[1]) * t]);
        }
      }
    } else {
      let segs = 32;
      for (let i = 0; i < segs; i++) {
        let a = (i / segs) * Math.PI * 2;
        pts.push([wx0 + Math.cos(a) * r, wy0 + Math.sin(a) * r]);
      }
    }

    let started = false;
    for (let i = 0; i < pts.length; i++) {
      let px = pts[i][0];
      let py = pts[i][1];
      let pz = (hasHeight ? viewer.map.heightAt(px, py) : 0) + 6;
      let sp = projectWorld(px, py, pz, vp);
      if (!sp) continue;
      if (!started) {
        overlayCtx.moveTo(sp[0], sp[1]);
        started = true;
      } else {
        overlayCtx.lineTo(sp[0], sp[1]);
      }
    }
    if (started) {
      overlayCtx.closePath();
      overlayCtx.stroke();
    }
    overlayCtx.restore();
  }

  function flushPendingDab() {
    brushState.dabRafId = 0;
    if (!cameraState.isPainting || !brushState.pendingDab) return;

    let { wx, wy } = brushState.pendingDab;
    let now = performance.now();
    let dt = now - brushState.lastDabTime;
    let dist = Math.hypot(wx - brushState.lastDabWx, wy - brushState.lastDabWy);
    let minStepDist = Math.max(24, Math.min(96, (brushState.size || 2) * 28));

    // Backpressure adaptativo si hay repintados pendientes en WebGL
    if (brushState.inFlightCount > 1 && dt < 45) {
      brushState.dabRafId = requestAnimationFrame(flushPendingDab);
      return;
    }

    if (dist < minStepDist && dt < 28) {
      return;
    }

    brushState.pendingDab = null;
    brushState.lastDabTime = now;
    brushState.lastDabWx = wx;
    brushState.lastDabWy = wy;
    postMsg({
      type: 'terrain_brush_dab',
      wx: wx,
      wy: wy,
      isStrokeStart: false
    });
  }

  function sendBrushDab(wx, wy, isStrokeStart) {
    if (isStrokeStart) {
      if (brushState.dabRafId) {
        cancelAnimationFrame(brushState.dabRafId);
        brushState.dabRafId = 0;
      }
      brushState.pendingDab = null;
      brushState.lastDabTime = performance.now();
      brushState.lastDabWx = wx;
      brushState.lastDabWy = wy;
      postMsg({
        type: 'terrain_brush_dab',
        wx: wx,
        wy: wy,
        isStrokeStart: true
      });
      return;
    }

    brushState.pendingDab = { wx, wy };
    if (!brushState.dabRafId) {
      brushState.dabRafId = requestAnimationFrame(flushPendingDab);
    }
  }

  // Mouse camera interactions + Terrain Brush + Unit/Doodad Editing
  canvas.addEventListener('contextmenu', (e) => e.preventDefault());

  canvas.addEventListener('mousedown', (e) => {
    e.preventDefault();
    canvas.focus();
    cameraState.lastX = e.clientX;
    cameraState.lastY = e.clientY;
    cameraState.downX = e.clientX;
    cameraState.downY = e.clientY;

    if (e.button === 1 || e.button === 2) {
      cameraState.isPanning = true;
      cameraState.rightDownX = e.clientX;
      cameraState.rightDownY = e.clientY;
      return;
    }

    if (e.button !== 0) return;

    // Terrain Paint Mode (hold Ctrl/Alt to orbit freely)
    if (brushState.active && !e.ctrlKey && !e.altKey) {
      let hit = raycastTerrain(e.clientX, e.clientY);
      if (hit) {
        brushState.hoverWx = hit[0];
        brushState.hoverWy = hit[1];
        cameraState.isPainting = true;
        sendBrushDab(hit[0], hit[1], true);
      }
      return;
    }

    // Placement Mode (Unit / Doodad)
    if ((editorState.mode === 'place_unit' || editorState.mode === 'place_doodad') && !e.ctrlKey && !e.altKey) {
      let hit = raycastTerrain(e.clientX, e.clientY);
      if (hit && editorState.placeSpec) {
        postMsg({
          type: 'entity_place_request',
          kind: editorState.placeSpec.kind,
          rawcode: editorState.placeSpec.rawcode,
          x: hit[0],
          y: hit[1],
          z: hit[2],
          angle: editorState.placeSpec.angle ?? (270 * Math.PI) / 180,
          scale: editorState.placeSpec.scale ?? 1,
          player: editorState.placeSpec.player ?? 0,
          variation: editorState.placeSpec.variation ?? 0
        });
      }
      return;
    }

    // Select / Drag Move Mode
    if (!e.ctrlKey && !e.altKey && viewer && viewer.map && typeof viewer.map.pickEntityAtRay === 'function') {
      let ray = raycastRay(e.clientX, e.clientY);
      if (ray) {
        let picked = viewer.map.pickEntityAtRay(ray[0], ray[1]);
        if (picked) {
          editorState.selectedEntity = picked;
          editorState.isDraggingEntity = true;
          editorState.dragMoved = false;
          postMsg({ type: 'entity_selected', entity: picked });
          return;
        }
      }
    }

    cameraState.isRotating = true;
  });

  window.addEventListener('mouseup', (e) => {
    if (e.button === 2 &&
        (editorState.mode === 'place_unit' || editorState.mode === 'place_doodad') &&
        Math.hypot(e.clientX - cameraState.rightDownX, e.clientY - cameraState.rightDownY) < 5) {
      editorState.mode = 'select';
      editorState.placeSpec = null;
      canvas.style.cursor = 'default';
      postMsg({ type: 'placement_cancelled' });
    }

    if (e.button === 0 && cameraState.isRotating &&
        Math.hypot(e.clientX - cameraState.downX, e.clientY - cameraState.downY) < 4 &&
        !e.ctrlKey && !e.altKey && editorState.selectedEntity) {
      editorState.selectedEntity = null;
      postMsg({ type: 'entity_selected', entity: null });
    }

    if (editorState.isDraggingEntity && editorState.dragMoved && editorState.selectedEntity) {
      postMsg({
        type: 'entity_moved',
        kind: editorState.selectedEntity.kind,
        creationNumber: editorState.selectedEntity.creationNumber,
        x: editorState.selectedEntity.x,
        y: editorState.selectedEntity.y,
        z: editorState.selectedEntity.z
      });
    }

    cameraState.isRotating = false;
    cameraState.isPanning = false;
    cameraState.isPainting = false;
    if (brushState.dabRafId) {
      cancelAnimationFrame(brushState.dabRafId);
      brushState.dabRafId = 0;
    }
    brushState.pendingDab = null;
    editorState.isDraggingEntity = false;
    editorState.dragMoved = false;
  });

  window.addEventListener('mouseleave', () => {
    brushState.hoverWx = null;
    brushState.hoverWy = null;
    editorState.hoverWx = null;
    editorState.hoverWy = null;
  });

  window.addEventListener('keydown', (e) => {
    if (e.key === 'Delete' && editorState.selectedEntity && editorState.mode === 'select') {
      postMsg({
        type: 'entity_delete_request',
        kind: editorState.selectedEntity.kind,
        creationNumber: editorState.selectedEntity.creationNumber
      });
    } else if (e.key === 'Escape' && (editorState.mode === 'place_unit' || editorState.mode === 'place_doodad')) {
      editorState.mode = 'select';
      editorState.placeSpec = null;
      canvas.style.cursor = 'default';
      postMsg({ type: 'placement_cancelled' });
    }
  });

  window.addEventListener('mousemove', (e) => {
    if (brushState.active && !cameraState.isRotating && !cameraState.isPanning) {
      let hit = raycastTerrain(e.clientX, e.clientY);
      if (hit) {
        brushState.hoverWx = hit[0];
        brushState.hoverWy = hit[1];
        if (cameraState.isPainting) {
          sendBrushDab(hit[0], hit[1], false);
        }
      }
    } else if ((editorState.mode === 'place_unit' || editorState.mode === 'place_doodad') &&
               !cameraState.isRotating && !cameraState.isPanning) {
      let hit = raycastTerrain(e.clientX, e.clientY);
      if (hit) {
        editorState.hoverWx = hit[0];
        editorState.hoverWy = hit[1];
      }
    } else if (editorState.isDraggingEntity && editorState.selectedEntity) {
      if (Math.hypot(e.clientX - cameraState.downX, e.clientY - cameraState.downY) >= 4) {
        let hit = raycastTerrain(e.clientX, e.clientY);
        if (hit && viewer && viewer.map) {
          editorState.dragMoved = true;
          editorState.selectedEntity.x = hit[0];
          editorState.selectedEntity.y = hit[1];
          editorState.selectedEntity.z = hit[2];
          if (editorState.selectedEntity.kind === 'unit' && typeof viewer.map.updateLiveUnit === 'function') {
            viewer.map.updateLiveUnit(editorState.selectedEntity.creationNumber, { x: hit[0], y: hit[1], z: hit[2] });
          } else if (editorState.selectedEntity.kind === 'doodad' && typeof viewer.map.updateLiveDoodad === 'function') {
            viewer.map.updateLiveDoodad(editorState.selectedEntity.creationNumber, { x: hit[0], y: hit[1], z: hit[2] });
          }
        }
      }
      return;
    }

    if (!cameraState.isRotating && !cameraState.isPanning) return;

    let dx = e.clientX - cameraState.lastX;
    let dy = e.clientY - cameraState.lastY;
    cameraState.lastX = e.clientX;
    cameraState.lastY = e.clientY;

    if (cameraState.isRotating) {
      cameraState.horizontalAngle -= dx * 0.008;
      cameraState.verticalAngle -= dy * 0.008;
      updateCamera();
    } else if (cameraState.isPanning) {
      if (!viewer || !viewer.map || !viewer.map.worldScene) return;
      let scene = viewer.map.worldScene;
      let dirX = scene.camera.directionX;
      let dirY = scene.camera.directionY;
      let lenX = Math.hypot(dirX[0], dirX[1]) || 1;
      let lenY = Math.hypot(dirY[0], dirY[1]) || 1;
      let w = Math.max(canvas.width, 1);
      let h = Math.max(canvas.height, 1);
      let aspect = w / h;
      let sw = (-dx / w) * cameraState.distance * aspect * 1.1;
      let sh = (dy / h) * cameraState.distance * 1.1;

      cameraState.target[0] += (dirX[0] / lenX) * sw + (dirY[0] / lenY) * sh;
      cameraState.target[1] += (dirX[1] / lenX) * sw + (dirY[1] / lenY) * sh;
      if (typeof viewer.map.heightAt === 'function') {
        cameraState.target[2] = viewer.map.heightAt(cameraState.target[0], cameraState.target[1]);
      }
      updateCamera();
    }
  });

  canvas.addEventListener('wheel', (e) => {
    e.preventDefault();
    let delta = e.deltaY > 0 ? 1 : -1;
    cameraState.distance = Math.max(300, Math.min(18000, cameraState.distance * (1 + delta * 0.12)));
    updateCamera();
  });

  function centerTargetOnMap() {
    if (viewer && viewer.map && viewer.map.centerOffset && viewer.map.mapSize) {
      let cx = viewer.map.centerOffset[0] + (viewer.map.mapSize[0] - 1) * 64;
      let cy = viewer.map.centerOffset[1] + (viewer.map.mapSize[1] - 1) * 64;
      let cz = typeof viewer.map.heightAt === 'function' ? viewer.map.heightAt(cx, cy) : 0;
      cameraState.target = [cx, cy, cz];
    }
  }

  function resetCamera() {
    cameraState.horizontalAngle = 0;
    cameraState.verticalAngle = Math.PI / 4;
    cameraState.distance = 3200;
    centerTargetOnMap();
    updateCamera();
  }

  function clearMap() {
    editorState.selectedEntity = null;
    if (viewer && viewer.map) {
      try { viewer.map.die(); } catch (e) {}
      viewer.map = null;
    }
    if (noMapHint) noMapHint.style.display = 'flex';
    if (loadingOverlay) loadingOverlay.style.display = 'none';
  }

  function loadMapFromUrl(url, title) {
    editorState.selectedEntity = null;
    if (noMapHint) noMapHint.style.display = 'none';
    if (loadingOverlay) loadingOverlay.style.display = 'block';
    if (loadingSpinner) loadingSpinner.textContent = 'Cargando Terreno y Modelos 3D...';
    if (loadingDetails) loadingDetails.textContent = 'Descargando datos del mapa...';

    fetch(url)
      .then((res) => {
        if (!res.ok) throw new Error('HTTP ' + res.status);
        return res.arrayBuffer();
      })
      .then((buffer) => {
        if (!buffer || buffer.byteLength === 0) {
          clearMap();
          return;
        }

        if (loadingDetails) loadingDetails.textContent = 'Procesando terreno, acantilados y doodads...';
        viewer.loadMap(buffer);

        centerTargetOnMap();
        resizeCanvas();
        updateCamera();
        if (loadingOverlay) loadingOverlay.style.display = 'none';
        if (noMapHint) noMapHint.style.display = 'none';

        postMsg({ type: 'map_loaded_success' });
      })
      .catch((err) => {
        console.error('Error al cargar mapa:', err);
        if (loadingDetails) loadingDetails.textContent = 'Error cargando mapa: ' + err.message;
      });
  }

  // Base files listener
  viewer.on('loadedbasefiles', () => {
    postMsg({ type: 'map_engine_ready' });
  });

  // Listen to C# messages
  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener('message', (event) => {
      let data = event.data;
      if (!data) return;

      if (data.action === 'load_map') {
        loadMapFromUrl(data.url || 'https://w3studio.local/currentmap.w3x', data.title);
      } else if (data.action === 'clear_map') {
        clearMap();
      } else if (data.action === 'reset_camera') {
        resetCamera();
      } else if (data.action === 'set_terrain_brush') {
        brushState.active = !!data.active;
        brushState.size = typeof data.size === 'number' ? data.size : 2;
        brushState.isSquare = !!data.isSquare;
        if (brushState.active) {
          editorState.mode = 'terrain';
          editorState.placeSpec = null;
          canvas.style.cursor = 'crosshair';
        } else {
          if (editorState.mode === 'terrain') editorState.mode = 'select';
          brushState.hoverWx = null;
          brushState.hoverWy = null;
          canvas.style.cursor = (editorState.mode === 'place_unit' || editorState.mode === 'place_doodad') ? 'crosshair' : 'default';
        }
      } else if (data.action === 'set_editor_mode') {
        editorState.mode = data.mode || 'select';
        editorState.placeSpec = data.placeSpec || null;
        brushState.active = editorState.mode === 'terrain';
        canvas.style.cursor = (editorState.mode === 'select') ? 'default' : 'crosshair';
        if (editorState.mode !== 'place_unit' && editorState.mode !== 'place_doodad') {
          editorState.hoverWx = null;
          editorState.hoverWy = null;
        }
      } else if (data.action === 'select_entity') {
        if (viewer && viewer.map && typeof viewer.map.getEntityTransform === 'function') {
          let ent = viewer.map.getEntityTransform(data.kind, data.creationNumber);
          editorState.selectedEntity = ent;
          if (ent && data.focus) {
            cameraState.target = [ent.x, ent.y, ent.z || 0];
            if (typeof data.distance === 'number') cameraState.distance = data.distance;
            updateCamera();
          }
        }
      } else if (data.action === 'focus_world_point') {
        let hz = (viewer && viewer.map && typeof viewer.map.heightAt === 'function')
          ? viewer.map.heightAt(data.x, data.y)
          : 0;
        cameraState.target = [data.x, data.y, hz];
        if (typeof data.distance === 'number') cameraState.distance = data.distance;
        updateCamera();
      } else if (data.action === 'spawn_entity') {
        if (viewer && viewer.map) {
          if (data.kind === 'unit' && typeof viewer.map.addLiveUnit === 'function') {
            viewer.map.addLiveUnit(data.opts);
          } else if (data.kind === 'doodad' && typeof viewer.map.addLiveDoodad === 'function') {
            viewer.map.addLiveDoodad(data.opts);
          }
          editorState.selectedEntity = {
            kind: data.kind,
            creationNumber: data.opts.creationNumber,
            id: data.opts.id,
            x: data.opts.x,
            y: data.opts.y,
            z: data.opts.z || 0,
            angle: data.opts.angle || 0,
            scale: data.opts.scale || 1,
            player: data.opts.player || 0
          };
        }
      } else if (data.action === 'update_entity') {
        if (viewer && viewer.map) {
          if (data.kind === 'unit' && typeof viewer.map.updateLiveUnit === 'function') {
            viewer.map.updateLiveUnit(data.creationNumber, data.opts);
          } else if (data.kind === 'doodad' && typeof viewer.map.updateLiveDoodad === 'function') {
            viewer.map.updateLiveDoodad(data.creationNumber, data.opts);
          }
          if (editorState.selectedEntity && editorState.selectedEntity.creationNumber === data.creationNumber) {
            Object.assign(editorState.selectedEntity, data.opts);
          }
        }
      } else if (data.action === 'delete_entity') {
        if (viewer && viewer.map) {
          if (data.kind === 'unit' && typeof viewer.map.removeLiveUnit === 'function') {
            viewer.map.removeLiveUnit(data.creationNumber);
          } else if (data.kind === 'doodad' && typeof viewer.map.removeLiveDoodad === 'function') {
            viewer.map.removeLiveDoodad(data.creationNumber);
          }
          if (editorState.selectedEntity && editorState.selectedEntity.creationNumber === data.creationNumber) {
            editorState.selectedEntity = null;
          }
        }
      } else if (data.action === 'patch_w3e_corners' && Array.isArray(data.patches)) {
        if (viewer && viewer.map && typeof viewer.map.patchW3eCorners === 'function') {
          brushState.inFlightCount++;
          brushState.reloadQueue = brushState.reloadQueue
            .then(() => viewer.map && viewer.map.patchW3eCorners(
              data.patches,
              data.minX,
              data.maxX,
              data.minY,
              data.maxY,
              !!data.cliffsChanged
            ))
            .catch((err) => console.error('Error parcheando w3e:', err))
            .finally(() => {
              brushState.inFlightCount = Math.max(0, brushState.inFlightCount - 1);
            });
        }
      } else if (data.action === 'reload_w3e' && data.base64) {
        if (viewer && viewer.map && typeof viewer.map.reloadW3eTerrain === 'function') {
          let binStr = atob(data.base64);
          let bytes = new Uint8Array(binStr.length);
          for (let i = 0; i < binStr.length; i++) {
            bytes[i] = binStr.charCodeAt(i);
          }
          brushState.reloadQueue = brushState.reloadQueue
            .then(() => viewer.map && viewer.map.reloadW3eTerrain(bytes.buffer))
            .catch((err) => console.error('Error recargando w3e:', err));
        }
      }
    });

    postMsg({ type: 'map_engine_script_loaded' });
  }

  // Render loop
  function step() {
    requestAnimationFrame(step);
    viewer.updateAndRender();
    drawViewportOverlays();
  }
  requestAnimationFrame(step);

})();
