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
  const loadingBarFill = document.getElementById('loading-bar-fill');
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
    mode: 'select', // 'select' | 'terrain' | 'place_unit' | 'place_doodad' | 'select_region' | 'place_region'
    placeSpec: null, // { kind, rawcode, player, angle, scale, variation, life, modelPath, pathTex, isBuilding, ... }
    selectedEntity: null, // { kind, creationNumber, id, x, y, z, angle, scale, player, variation, life }
    isDraggingEntity: false,
    dragMoved: false,
    dragOrigPos: null,
    dragLastValidPos: null,
    placementEval: null,
    ignorePathing: false,
    hoverWx: null,
    hoverWy: null
  };

  const layerState = {
    showUnits: true,
    showDoodads: true,
    showRegions: false,
    showGrid: false,
    showPathing: false,
    showBounds: false,
    showWeather: true,
    weatherOverride: ''
  };

  const regionState = {
    regions: [],
    selectedNumber: null,
    dragMode: null, // 'move' | 'nw' | 'ne' | 'sw' | 'se' | 'create'
    dragStartWx: 0,
    dragStartWy: 0,
    origRect: null,
    createDraft: null
  };

  function applyLayerVisibility() {
    if (!viewer || !viewer.map) return;
    if (Array.isArray(viewer.map.units)) {
      for (let u of viewer.map.units) {
        if (u && u.instance) {
          if (layerState.showUnits) u.instance.show();
          else u.instance.hide();
        }
      }
    }
    if (Array.isArray(viewer.map.doodads)) {
      for (let d of viewer.map.doodads) {
        if (d && d.instance) {
          if (layerState.showDoodads) d.instance.show();
          else d.instance.hide();
        }
      }
    }
    if (Array.isArray(viewer.map.terrainDoodads)) {
      for (let td of viewer.map.terrainDoodads) {
        if (td && td.instance) {
          if (layerState.showDoodads) td.instance.show();
          else td.instance.hide();
        }
      }
    }
  }

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

  function drawProjectedSegment(ax, ay, bx, by, subSegs, hasHeight, vp) {
    let started = false;
    for (let s = 0; s <= subSegs; s++) {
      let t = s / subSegs;
      let px = ax + (bx - ax) * t;
      let py = ay + (by - ay) * t;
      let pz = (hasHeight ? viewer.map.heightAt(px, py) : 0) + 6;
      let sp = projectWorld(px, py, pz, vp);
      if (!sp) {
        started = false;
        continue;
      }
      if (!started) {
        overlayCtx.moveTo(sp[0], sp[1]);
        started = true;
      } else {
        overlayCtx.lineTo(sp[0], sp[1]);
      }
    }
  }

  function drawTerrainGridOverlay(vp, hasHeight) {
    if (!viewer || !viewer.map || !viewer.map.centerOffset || !viewer.map.mapSize) return;
    let ox = viewer.map.centerOffset[0];
    let oy = viewer.map.centerOffset[1];
    let maxWx = ox + (viewer.map.mapSize[0] - 1) * 128;
    let maxWy = oy + (viewer.map.mapSize[1] - 1) * 128;

    let step = cameraState.distance > 5200 ? 512 : 128;
    let span = Math.min(4608, Math.max(1792, cameraState.distance * 1.15));
    let minX = Math.max(ox, Math.floor((cameraState.target[0] - span - ox) / step) * step + ox);
    let maxX = Math.min(maxWx, Math.ceil((cameraState.target[0] + span - ox) / step) * step + ox);
    let minY = Math.max(oy, Math.floor((cameraState.target[1] - span - oy) / step) * step + oy);
    let maxY = Math.min(maxWy, Math.ceil((cameraState.target[1] + span - oy) / step) * step + oy);
    let sub = Math.max(4, Math.round((maxX - minX) / 512));

    overlayCtx.save();
    overlayCtx.lineWidth = 1;
    overlayCtx.strokeStyle = 'rgba(255, 255, 255, 0.22)';
    overlayCtx.beginPath();
    for (let x = minX; x <= maxX; x += step) {
      if (Math.round(x - ox) % 512 === 0) continue;
      drawProjectedSegment(x, minY, x, maxY, sub, hasHeight, vp);
    }
    for (let y = minY; y <= maxY; y += step) {
      if (Math.round(y - oy) % 512 === 0) continue;
      drawProjectedSegment(minX, y, maxX, y, sub, hasHeight, vp);
    }
    overlayCtx.stroke();

    overlayCtx.strokeStyle = 'rgba(110, 215, 255, 0.44)';
    overlayCtx.lineWidth = 1.3;
    overlayCtx.beginPath();
    let majorMinX = Math.max(ox, Math.floor((minX - ox) / 512) * 512 + ox);
    let majorMinY = Math.max(oy, Math.floor((minY - oy) / 512) * 512 + oy);
    for (let x = majorMinX; x <= maxX; x += 512) {
      drawProjectedSegment(x, minY, x, maxY, sub, hasHeight, vp);
    }
    for (let y = majorMinY; y <= maxY; y += 512) {
      drawProjectedSegment(minX, y, maxX, y, sub, hasHeight, vp);
    }
    overlayCtx.stroke();
    overlayCtx.restore();
  }

  function buildRectPerimeterPoints(l, b, r, t) {
    let minX = Math.min(l, r), maxX = Math.max(l, r);
    let minY = Math.min(b, t), maxY = Math.max(b, t);
    let corners = [[minX, minY], [maxX, minY], [maxX, maxY], [minX, maxY]];
    let pts = [];
    for (let e = 0; e < 4; e++) {
      let cA = corners[e], cB = corners[(e + 1) % 4];
      for (let s = 0; s < 4; s++) {
        let f = s / 4;
        pts.push([cA[0] + (cB[0] - cA[0]) * f, cA[1] + (cB[1] - cA[1]) * f]);
      }
    }
    return pts;
  }

  function drawRegionsOverlay(vp, hasHeight) {
    let showAll = layerState.showRegions || editorState.mode === 'select_region' || editorState.mode === 'place_region';
    if (!showAll && regionState.selectedNumber === null && !regionState.createDraft) return;

    const renderRect = (reg, isSelected, isDraft) => {
      let pts = buildRectPerimeterPoints(reg.left, reg.bottom, reg.right, reg.top);
      let screenPts = [];
      for (let p of pts) {
        let pz = (hasHeight ? viewer.map.heightAt(p[0], p[1]) : 0) + 12;
        let sp = projectWorld(p[0], p[1], pz, vp);
        if (sp) screenPts.push(sp);
      }
      if (screenPts.length < 3) return;

      let r = typeof reg.colorR === 'number' ? reg.colorR : 96;
      let g = typeof reg.colorG === 'number' ? reg.colorG : 185;
      let b = typeof reg.colorB === 'number' ? reg.colorB : 255;

      overlayCtx.save();
      overlayCtx.beginPath();
      overlayCtx.moveTo(screenPts[0][0], screenPts[0][1]);
      for (let i = 1; i < screenPts.length; i++) {
        overlayCtx.lineTo(screenPts[i][0], screenPts[i][1]);
      }
      overlayCtx.closePath();

      overlayCtx.fillStyle = isDraft
        ? 'rgba(0, 255, 200, 0.22)'
        : `rgba(${r}, ${g}, ${b}, ${isSelected ? 0.34 : 0.18})`;
      overlayCtx.fill();

      if (isDraft) overlayCtx.setLineDash([6, 4]);
      overlayCtx.strokeStyle = isDraft
        ? '#00FFE5'
        : isSelected
          ? '#FFE44D'
          : `rgb(${r}, ${g}, ${b})`;
      overlayCtx.lineWidth = isSelected ? 2.6 : 1.6;
      overlayCtx.stroke();
      overlayCtx.setLineDash([]);

      let cx = (reg.left + reg.right) * 0.5;
      let cy = (reg.bottom + reg.top) * 0.5;
      let cz = (hasHeight ? viewer.map.heightAt(cx, cy) : 0) + 14;
      let labelPt = projectWorld(cx, cy, cz, vp);
      if (labelPt && reg.name) {
        overlayCtx.font = 'bold 11px Segoe UI, sans-serif';
        let tw = overlayCtx.measureText(reg.name).width;
        overlayCtx.fillStyle = 'rgba(14, 18, 24, 0.78)';
        overlayCtx.fillRect(labelPt[0] - tw * 0.5 - 5, labelPt[1] - 9, tw + 10, 17);
        overlayCtx.fillStyle = isSelected ? '#FFE44D' : '#F2F6FA';
        overlayCtx.textAlign = 'center';
        overlayCtx.textBaseline = 'middle';
        overlayCtx.fillText(reg.name, labelPt[0], labelPt[1]);
      }

      if (isSelected && !isDraft) {
        let handles = [
          [Math.min(reg.left, reg.right), Math.max(reg.bottom, reg.top)],
          [Math.max(reg.left, reg.right), Math.max(reg.bottom, reg.top)],
          [Math.min(reg.left, reg.right), Math.min(reg.bottom, reg.top)],
          [Math.max(reg.left, reg.right), Math.min(reg.bottom, reg.top)]
        ];
        for (let h of handles) {
          let hz = (hasHeight ? viewer.map.heightAt(h[0], h[1]) : 0) + 12;
          let hp = projectWorld(h[0], h[1], hz, vp);
          if (!hp) continue;
          overlayCtx.fillStyle = '#FFFFFF';
          overlayCtx.strokeStyle = '#111111';
          overlayCtx.lineWidth = 1.5;
          overlayCtx.fillRect(hp[0] - 4, hp[1] - 4, 8, 8);
          overlayCtx.strokeRect(hp[0] - 4, hp[1] - 4, 8, 8);
        }
      }
      overlayCtx.restore();
    };

    for (let reg of regionState.regions) {
      let isSel = reg.creationNumber === regionState.selectedNumber;
      if (!showAll && !isSel) continue;
      renderRect(reg, isSel, false);
    }
    if (regionState.createDraft) {
      renderRect(regionState.createDraft, true, true);
    }
  }

  function findRegionHandleOrHit(wx, wy) {
    let tol = Math.max(96, cameraState.distance * 0.045);
    let selected = regionState.regions.find(r => r.creationNumber === regionState.selectedNumber);
    if (selected) {
      let minX = Math.min(selected.left, selected.right), maxX = Math.max(selected.left, selected.right);
      let minY = Math.min(selected.bottom, selected.top), maxY = Math.max(selected.bottom, selected.top);
      let corners = [
        ['nw', minX, maxY],
        ['ne', maxX, maxY],
        ['sw', minX, minY],
        ['se', maxX, minY]
      ];
      for (let c of corners) {
        if (Math.hypot(wx - c[1], wy - c[2]) <= tol) {
          return { region: selected, mode: c[0] };
        }
      }
    }

    let best = null;
    let bestArea = Infinity;
    for (let reg of regionState.regions) {
      let minX = Math.min(reg.left, reg.right), maxX = Math.max(reg.left, reg.right);
      let minY = Math.min(reg.bottom, reg.top), maxY = Math.max(reg.bottom, reg.top);
      if (wx >= minX && wx <= maxX && wy >= minY && wy <= maxY) {
        let area = Math.max(1, (maxX - minX) * (maxY - minY));
        if (area < bestArea) {
          bestArea = area;
          best = reg;
        }
      }
    }
    return best ? { region: best, mode: 'move' } : null;
  }

  const weatherParticles = [];
  let weatherLastTs = 0;

  function drawBoundsOverlay(vp, hasHeight) {
    if (!viewer || !viewer.map || !viewer.map.centerOffset || !viewer.map.mapSize) return;
    const vis = viewer.map._w3sVisuals;
    const ox = viewer.map.centerOffset[0];
    const oy = viewer.map.centerOffset[1];
    const fullMaxX = ox + (viewer.map.mapSize[0] - 1) * 128;
    const fullMaxY = oy + (viewer.map.mapSize[1] - 1) * 128;

    const strokeWorldBox = (minX, minY, maxX, maxY, color, width, dashed, label) => {
      const pts = buildRectPerimeterPoints(minX, minY, maxX, maxY);
      const spList = [];
      for (const p of pts) {
        const pz = (hasHeight ? viewer.map.heightAt(p[0], p[1]) : 0) + 18;
        spList.push(projectWorld(p[0], p[1], pz, vp));
      }
      if (spList.some(s => !s)) return;
      overlayCtx.save();
      overlayCtx.strokeStyle = color;
      overlayCtx.lineWidth = width;
      if (dashed) overlayCtx.setLineDash([8, 5]);
      overlayCtx.beginPath();
      overlayCtx.moveTo(spList[0][0], spList[0][1]);
      for (let i = 1; i < spList.length; i++) overlayCtx.lineTo(spList[i][0], spList[i][1]);
      overlayCtx.closePath();
      overlayCtx.stroke();
      if (label) {
        const cx = (minX + maxX) * 0.5;
        const cz = (hasHeight ? viewer.map.heightAt(cx, maxY) : 0) + 28;
        const lp = projectWorld(cx, maxY, cz, vp);
        if (lp) {
          overlayCtx.setLineDash([]);
          overlayCtx.font = 'bold 11px Segoe UI, sans-serif';
          overlayCtx.fillStyle = color;
          overlayCtx.textAlign = 'center';
          overlayCtx.fillText(label, lp[0], lp[1] - 4);
        }
      }
      overlayCtx.restore();
    };

    if (vis && Array.isArray(vis.complements)) {
      const [cL, cR, cB, cT] = vis.complements;
      const pMinX = ox + Math.max(0, cL) * 128;
      const pMaxX = fullMaxX - Math.max(0, cR) * 128;
      const pMinY = oy + Math.max(0, cB) * 128;
      const pMaxY = fullMaxY - Math.max(0, cT) * 128;
      if (pMaxX > pMinX && pMaxY > pMinY) {
        strokeWorldBox(pMinX, pMinY, pMaxX, pMaxY, 'rgba(255, 212, 70, 0.88)', 1.8, true, 'Playable Map Area');
      }
    }

    if (vis && vis.cameraBounds) {
      const cb = vis.cameraBounds;
      strokeWorldBox(cb.minX, cb.minY, cb.maxX, cb.maxY, 'rgba(64, 215, 255, 0.95)', 2.2, false, 'Camera Bounds');
    }
  }

  function drawPathingOverlay(vp, hasHeight) {
    if (!viewer || !viewer.map || !viewer.map._w3sPathing || !viewer.map.centerOffset) return;
    const wpm = viewer.map._w3sPathing;
    const ox = viewer.map.centerOffset[0];
    const oy = viewer.map.centerOffset[1];
    const subSize = 32;
    const maxRadius = Math.min(1800, Math.max(768, cameraState.distance * 0.65));
    const minIx = Math.max(0, Math.floor((cameraState.target[0] - maxRadius - ox) / subSize));
    const maxIx = Math.min(wpm.width - 1, Math.ceil((cameraState.target[0] + maxRadius - ox) / subSize));
    const minIy = Math.max(0, Math.floor((cameraState.target[1] - maxRadius - oy) / subSize));
    const maxIy = Math.min(wpm.height - 1, Math.ceil((cameraState.target[1] + maxRadius - oy) / subSize));

    const spanCells = Math.max(1, (maxIx - minIx) * (maxIy - minIy));
    const stride = spanCells > 2600 ? 3 : spanCells > 1100 ? 2 : 1;
    const halfDot = Math.max(2.2, Math.min(6, (4200 / Math.max(cameraState.distance, 400)) * stride));

    overlayCtx.save();
    for (let iy = minIy; iy <= maxIy; iy += stride) {
      const rowOff = iy * wpm.width;
      const wy = oy + (iy + 0.5) * subSize;
      for (let ix = minIx; ix <= maxIx; ix += stride) {
        const flag = wpm.cells[rowOff + ix];
        // WC3 war3map.wpm bits: 0x02 no-walk, 0x04 no-fly, 0x08 no-build
        if ((flag & 0x0e) === 0) continue;
        const wx = ox + (ix + 0.5) * subSize;
        const wz = (hasHeight ? viewer.map.heightAt(wx, wy) : 0) + 6;
        const sp = projectWorld(wx, wy, wz, vp);
        if (!sp || sp[0] < -16 || sp[1] < -16 || sp[0] > overlayCanvas.width + 16 || sp[1] > overlayCanvas.height + 16) continue;

        if (flag & 0x02) overlayCtx.fillStyle = 'rgba(235, 52, 52, 0.50)';
        else if (flag & 0x04) overlayCtx.fillStyle = 'rgba(215, 72, 235, 0.45)';
        else overlayCtx.fillStyle = 'rgba(245, 195, 45, 0.42)';

        overlayCtx.fillRect(sp[0] - halfDot, sp[1] - halfDot, halfDot * 2, halfDot * 2);
      }
    }
    overlayCtx.restore();
  }

  function drawWeatherOverlay(dt) {
    if (!layerState.showWeather || !viewer || !viewer.map) return;
    const vis = viewer.map._w3sVisuals;
    const rawCode = (layerState.weatherOverride || (vis && vis.globalWeather) || '').trim();
    if (!rawCode) return;

    const code = rawCode.toUpperCase();
    const w = overlayCanvas.width;
    const h = overlayCanvas.height;
    if (w <= 0 || h <= 0) return;

    // Atmospheric rays (Light / Moonlight)
    if (code.startsWith('LR')) {
      const isMoon = code === 'LRMA';
      overlayCtx.save();
      for (let i = 0; i < 4; i++) {
        const bx = w * (0.18 + i * 0.22);
        const grad = overlayCtx.createLinearGradient(bx, 0, bx + 110, h * 0.88);
        grad.addColorStop(0, isMoon ? 'rgba(170, 210, 255, 0.14)' : 'rgba(255, 244, 190, 0.15)');
        grad.addColorStop(1, 'rgba(255, 255, 255, 0.0)');
        overlayCtx.fillStyle = grad;
        overlayCtx.beginPath();
        overlayCtx.moveTo(bx - 35, 0);
        overlayCtx.lineTo(bx + 45, 0);
        overlayCtx.lineTo(bx + 170, h * 0.88);
        overlayCtx.lineTo(bx + 55, h * 0.88);
        overlayCtx.closePath();
        overlayCtx.fill();
      }
      overlayCtx.restore();
      return;
    }

    // Colored / Dungeon Fog wash (FD..)
    if (code.startsWith('FD')) {
      let rgb = '195, 205, 220';
      if (code.includes('R')) rgb = '220, 95, 80';
      else if (code.includes('G')) rgb = '95, 195, 115';
      else if (code.includes('B')) rgb = '95, 155, 230';
      const grad = overlayCtx.createRadialGradient(w * 0.5, h * 0.5, h * 0.2, w * 0.5, h * 0.5, Math.max(w, h) * 0.72);
      grad.addColorStop(0, `rgba(${rgb}, 0.03)`);
      grad.addColorStop(1, `rgba(${rgb}, ${code.endsWith('H') ? 0.22 : 0.13})`);
      overlayCtx.save();
      overlayCtx.fillStyle = grad;
      overlayCtx.fillRect(0, 0, w, h);
      overlayCtx.restore();
      return;
    }

    const isSnow = code.startsWith('SN');
    const isWind = code.startsWith('WO') || code.startsWith('WN') || code.startsWith('ME');
    const isHeavy = code.includes('H') || code === 'SNBS';
    const targetCount = isSnow ? (isHeavy ? 110 : 65) : isWind ? (isHeavy ? 75 : 45) : (isHeavy ? 140 : 80);

    while (weatherParticles.length < targetCount) {
      weatherParticles.push({
        x: Math.random() * (w + 160) - 80,
        y: Math.random() * h,
        speed: 0.7 + Math.random() * 0.6,
        phase: Math.random() * Math.PI * 2,
        len: 10 + Math.random() * 14
      });
    }
    if (weatherParticles.length > targetCount) {
      weatherParticles.length = targetCount;
    }

    overlayCtx.save();
    if (isSnow) {
      const vxBase = code === 'SNBS' ? -155 : -35;
      const vyBase = isHeavy ? 135 : 85;
      overlayCtx.fillStyle = 'rgba(242, 248, 255, 0.72)';
      for (const p of weatherParticles) {
        p.phase += dt * 2.4;
        p.x += (vxBase + Math.sin(p.phase) * 24) * p.speed * dt;
        p.y += vyBase * p.speed * dt;
        if (p.y > h + 8) { p.y = -8; p.x = Math.random() * (w + 120); }
        if (p.x < -30) p.x = w + 15;
        overlayCtx.beginPath();
        overlayCtx.arc(p.x, p.y, isHeavy ? 2.2 : 1.7, 0, Math.PI * 2);
        overlayCtx.fill();
      }
    } else if (isWind) {
      overlayCtx.strokeStyle = 'rgba(225, 240, 235, 0.34)';
      overlayCtx.lineWidth = 1.2;
      overlayCtx.beginPath();
      for (const p of weatherParticles) {
        p.x += (isHeavy ? 420 : 270) * p.speed * dt;
        p.y += Math.sin(p.phase += dt * 3) * 32 * dt;
        if (p.x > w + 40) { p.x = -40; p.y = Math.random() * h; }
        overlayCtx.moveTo(p.x, p.y);
        overlayCtx.lineTo(p.x - p.len * 2.1, p.y - 3);
      }
      overlayCtx.stroke();
    } else {
      // Rain (Ashenvale RA.., Lordaeron RL.., etc.)
      const vy = isHeavy ? 780 : 620;
      const vx = -140;
      overlayCtx.strokeStyle = isHeavy ? 'rgba(180, 215, 255, 0.42)' : 'rgba(180, 215, 255, 0.28)';
      overlayCtx.lineWidth = 1.1;
      overlayCtx.beginPath();
      for (const p of weatherParticles) {
        p.x += vx * p.speed * dt;
        p.y += vy * p.speed * dt;
        if (p.y > h + 20 || p.x < -40) {
          p.x = Math.random() * (w + 180);
          p.y = -20;
        }
        overlayCtx.moveTo(p.x, p.y);
        overlayCtx.lineTo(p.x + vx * 0.025, p.y + vy * 0.025);
      }
      overlayCtx.stroke();
    }
    overlayCtx.restore();
  }

  function drawPlacementCellsOverlay(evalRes, vp, hasHeight) {
    if (!evalRes || !Array.isArray(evalRes.cells) || evalRes.cells.length === 0 || !viewer || !viewer.map || !viewer.map.centerOffset) return;
    const ox = viewer.map.centerOffset[0];
    const oy = viewer.map.centerOffset[1];
    const inset = 1.5;

    overlayCtx.save();
    overlayCtx.lineWidth = 1;
    for (let i = 0; i < evalRes.cells.length; i++) {
      const c = evalRes.cells[i];
      const wx0 = ox + c.cx * 32 + inset;
      const wy0 = oy + c.cy * 32 + inset;
      const wx1 = ox + (c.cx + 1) * 32 - inset;
      const wy1 = oy + (c.cy + 1) * 32 - inset;
      const z00 = (hasHeight ? viewer.map.heightAt(wx0, wy0) : 0) + 7;
      const z10 = (hasHeight ? viewer.map.heightAt(wx1, wy0) : 0) + 7;
      const z11 = (hasHeight ? viewer.map.heightAt(wx1, wy1) : 0) + 7;
      const z01 = (hasHeight ? viewer.map.heightAt(wx0, wy1) : 0) + 7;

      const p0 = projectWorld(wx0, wy0, z00, vp);
      const p1 = projectWorld(wx1, wy0, z10, vp);
      const p2 = projectWorld(wx1, wy1, z11, vp);
      const p3 = projectWorld(wx0, wy1, z01, vp);
      if (!p0 || !p1 || !p2 || !p3) continue;

      overlayCtx.beginPath();
      overlayCtx.moveTo(p0[0], p0[1]);
      overlayCtx.lineTo(p1[0], p1[1]);
      overlayCtx.lineTo(p2[0], p2[1]);
      overlayCtx.lineTo(p3[0], p3[1]);
      overlayCtx.closePath();

      if (c.blocked) {
        overlayCtx.fillStyle = 'rgba(245, 45, 45, 0.54)';
        overlayCtx.strokeStyle = 'rgba(255, 95, 95, 0.85)';
      } else {
        overlayCtx.fillStyle = 'rgba(40, 225, 75, 0.34)';
        overlayCtx.strokeStyle = 'rgba(90, 255, 130, 0.72)';
      }
      overlayCtx.fill();
      overlayCtx.stroke();
    }
    overlayCtx.restore();
  }

  function drawViewportOverlays(nowMs) {
    if (!overlayCtx || !overlayCanvas) return;
    overlayCtx.clearRect(0, 0, overlayCanvas.width, overlayCanvas.height);

    const ts = typeof nowMs === 'number' ? nowMs : performance.now();
    const dt = weatherLastTs > 0 ? Math.min(0.05, Math.max(0.004, (ts - weatherLastTs) / 1000)) : 0.016;
    weatherLastTs = ts;

    if (!viewer || !viewer.map || !viewer.map.worldScene || !viewer.map.worldScene.camera) return;
    let vp = viewer.map.worldScene.camera.viewProjectionMatrix;
    let hasHeight = typeof viewer.map.heightAt === 'function';

    // 0. Terrain Grid (G), Pathing (P), Camera Bounds (B) & Regions (war3map.w3r)
    if (layerState.showGrid) {
      drawTerrainGridOverlay(vp, hasHeight);
    }
    if (layerState.showPathing) {
      drawPathingOverlay(vp, hasHeight);
    }
    if (layerState.showBounds) {
      drawBoundsOverlay(vp, hasHeight);
    }
    drawRegionsOverlay(vp, hasHeight);
    drawWeatherOverlay(dt);

    // 0b. Placement / Dragging WC3 32x32 Pathing Grid Footprint
    if ((editorState.isDraggingEntity && editorState.dragMoved && editorState.placementEval) ||
        ((editorState.mode === 'place_unit' || editorState.mode === 'place_doodad') && editorState.placementEval)) {
      drawPlacementCellsOverlay(editorState.placementEval, vp, hasHeight);
    }

    // 1. Selected Entity selection circle + facing vector
    if (editorState.selectedEntity) {
      let live = typeof viewer.map.getEntityTransform === 'function'
        ? viewer.map.getEntityTransform(editorState.selectedEntity.kind, editorState.selectedEntity.creationNumber)
        : null;
      let ent = live || editorState.selectedEntity;
      let rad = Math.max(52, 68 * (ent.scale || 1));
      let ringColor = (editorState.isDraggingEntity && editorState.placementEval && !editorState.placementEval.valid && !editorState.ignorePathing)
        ? '#FF3B3B'
        : (ent.kind === 'unit' ? '#33FF55' : '#40D8FF');
      drawWorldRing(ent.x, ent.y, ent.z || 0, rad, ringColor, 2.5, ent.angle, vp);
    }

    // 2. Placement ghost circle
    if ((editorState.mode === 'place_unit' || editorState.mode === 'place_doodad') &&
        editorState.hoverWx !== null && editorState.hoverWy !== null) {
      let hz = hasHeight ? viewer.map.heightAt(editorState.hoverWx, editorState.hoverWy) : 0;
      let pAngle = editorState.placeSpec ? editorState.placeSpec.angle : (270 * Math.PI) / 180;
      let pScale = editorState.placeSpec ? (editorState.placeSpec.scale || 1) : 1;
      let ghostColor = (editorState.placementEval && !editorState.placementEval.valid && !editorState.ignorePathing)
        ? '#FF3B3B'
        : '#00FFE5';
      drawWorldRing(editorState.hoverWx, editorState.hoverWy, hz, 64 * pScale, ghostColor, 2, pAngle, vp);
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
        let px = hit[0], py = hit[1], pz = hit[2];
        if (viewer && viewer.map && typeof viewer.map.evaluateEntityPlacement === 'function') {
          let evalRes = viewer.map.evaluateEntityPlacement(
            editorState.placeSpec.kind,
            editorState.placeSpec.rawcode,
            null,
            hit[0],
            hit[1],
            editorState.ignorePathing
          );
          editorState.placementEval = evalRes;
          if (!evalRes.valid && !editorState.ignorePathing) {
            return;
          }
          px = evalRes.x;
          py = evalRes.y;
          pz = evalRes.z;
        }
        postMsg({
          type: 'entity_place_request',
          kind: editorState.placeSpec.kind,
          rawcode: editorState.placeSpec.rawcode,
          x: px,
          y: py,
          z: pz,
          angle: editorState.placeSpec.angle ?? (270 * Math.PI) / 180,
          scale: editorState.placeSpec.scale ?? 1,
          player: editorState.placeSpec.player ?? 0,
          variation: editorState.placeSpec.variation ?? 0
        });
      }
      return;
    }

    // Region Create Mode (click-drag on terrain)
    if (editorState.mode === 'place_region' && !e.ctrlKey && !e.altKey) {
      let hit = raycastTerrain(e.clientX, e.clientY);
      if (hit) {
        regionState.dragMode = 'create';
        regionState.dragStartWx = hit[0];
        regionState.dragStartWy = hit[1];
        regionState.createDraft = {
          name: 'Nueva Región',
          left: hit[0] - 64,
          bottom: hit[1] - 64,
          right: hit[0] + 64,
          top: hit[1] + 64
        };
      }
      return;
    }

    // Region Select / Move / Resize when in Region Mode
    if (editorState.mode === 'select_region' && !e.ctrlKey && !e.altKey) {
      let hit = raycastTerrain(e.clientX, e.clientY);
      if (hit) {
        let rHit = findRegionHandleOrHit(hit[0], hit[1]);
        if (rHit) {
          let reg = rHit.region;
          regionState.selectedNumber = reg.creationNumber;
          regionState.dragMode = rHit.mode;
          regionState.dragStartWx = hit[0];
          regionState.dragStartWy = hit[1];
          regionState.origRect = {
            left: Math.min(reg.left, reg.right),
            right: Math.max(reg.left, reg.right),
            bottom: Math.min(reg.bottom, reg.top),
            top: Math.max(reg.bottom, reg.top)
          };
          editorState.dragMoved = false;
          postMsg({ type: 'region_selected', creationNumber: reg.creationNumber });
          return;
        } else if (regionState.selectedNumber !== null) {
          regionState.selectedNumber = null;
          postMsg({ type: 'region_selected', creationNumber: -1 });
        }
      }
    }

    // Select / Drag Move Mode (Units / Doodads)
    if (!e.ctrlKey && !e.altKey && viewer && viewer.map && typeof viewer.map.pickEntityAtRay === 'function') {
      let ray = raycastRay(e.clientX, e.clientY);
      if (ray) {
        let picked = viewer.map.pickEntityAtRay(ray[0], ray[1]);
        if (picked) {
          editorState.selectedEntity = picked;
          editorState.isDraggingEntity = true;
          editorState.dragMoved = false;
          editorState.dragOrigPos = { x: picked.x, y: picked.y, z: picked.z || 0 };
          editorState.dragLastValidPos = { x: picked.x, y: picked.y, z: picked.z || 0 };
          editorState.placementEval = null;
          postMsg({ type: 'entity_selected', entity: picked });
          return;
        }
      }
    }

    // Fallback: if Show Regions (R) is visible in normal Select mode, allow selecting/dragging a region on click
    if (layerState.showRegions && !e.ctrlKey && !e.altKey) {
      let hit = raycastTerrain(e.clientX, e.clientY);
      if (hit) {
        let rHit = findRegionHandleOrHit(hit[0], hit[1]);
        if (rHit) {
          let reg = rHit.region;
          regionState.selectedNumber = reg.creationNumber;
          regionState.dragMode = rHit.mode;
          regionState.dragStartWx = hit[0];
          regionState.dragStartWy = hit[1];
          regionState.origRect = {
            left: Math.min(reg.left, reg.right),
            right: Math.max(reg.left, reg.right),
            bottom: Math.min(reg.bottom, reg.top),
            top: Math.max(reg.bottom, reg.top)
          };
          editorState.dragMoved = false;
          postMsg({ type: 'region_selected', creationNumber: reg.creationNumber });
          return;
        }
      }
    }

    cameraState.isRotating = true;
  });

  window.addEventListener('mouseup', (e) => {
    if (e.button === 2 &&
        (editorState.mode === 'place_unit' || editorState.mode === 'place_doodad' || editorState.mode === 'place_region') &&
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

    if (regionState.dragMode === 'create' && regionState.createDraft) {
      let d = regionState.createDraft;
      let minX = Math.min(d.left, d.right), maxX = Math.max(d.left, d.right);
      let minY = Math.min(d.bottom, d.top), maxY = Math.max(d.bottom, d.top);
      if (maxX - minX < 128) { minX -= 128; maxX += 128; }
      if (maxY - minY < 128) { minY -= 128; maxY += 128; }
      regionState.createDraft = null;
      regionState.dragMode = null;
      postMsg({
        type: 'region_create_request',
        left: minX,
        bottom: minY,
        right: maxX,
        top: maxY
      });
    } else if (regionState.dragMode && regionState.selectedNumber !== null && editorState.dragMoved) {
      let reg = regionState.regions.find(r => r.creationNumber === regionState.selectedNumber);
      if (reg) {
        postMsg({
          type: 'region_updated',
          creationNumber: reg.creationNumber,
          left: Math.min(reg.left, reg.right),
          bottom: Math.min(reg.bottom, reg.top),
          right: Math.max(reg.left, reg.right),
          top: Math.max(reg.bottom, reg.top)
        });
      }
    }
    regionState.dragMode = null;
    regionState.origRect = null;
    regionState.createDraft = null;

    if (editorState.isDraggingEntity && editorState.dragMoved && editorState.selectedEntity) {
      let ent = editorState.selectedEntity;
      if (editorState.placementEval && !editorState.placementEval.valid && !editorState.ignorePathing) {
        let fallback = editorState.dragLastValidPos || editorState.dragOrigPos;
        if (fallback && viewer && viewer.map) {
          ent.x = fallback.x;
          ent.y = fallback.y;
          ent.z = fallback.z;
          if (ent.kind === 'unit' && typeof viewer.map.updateLiveUnit === 'function') {
            viewer.map.updateLiveUnit(ent.creationNumber, { x: fallback.x, y: fallback.y, z: fallback.z });
          } else if (ent.kind === 'doodad' && typeof viewer.map.updateLiveDoodad === 'function') {
            viewer.map.updateLiveDoodad(ent.creationNumber, { x: fallback.x, y: fallback.y, z: fallback.z });
          }
        }
      }
      let orig = editorState.dragOrigPos;
      if (!orig || Math.abs(ent.x - orig.x) > 0.5 || Math.abs(ent.y - orig.y) > 0.5) {
        postMsg({
          type: 'entity_moved',
          kind: ent.kind,
          creationNumber: ent.creationNumber,
          x: ent.x,
          y: ent.y,
          z: ent.z
        });
      }
    }
    editorState.dragOrigPos = null;
    editorState.dragLastValidPos = null;
    if (editorState.isDraggingEntity) editorState.placementEval = null;

    let wasPainting = cameraState.isPainting;
    cameraState.isRotating = false;
    cameraState.isPanning = false;
    cameraState.isPainting = false;
    if (brushState.dabRafId) {
      cancelAnimationFrame(brushState.dabRafId);
      brushState.dabRafId = 0;
    }
    brushState.pendingDab = null;
    if (wasPainting) {
      postMsg({ type: 'terrain_brush_end' });
    }
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
    } else if (e.key === 'Delete' && regionState.selectedNumber !== null &&
               (editorState.mode === 'select_region' || (editorState.mode === 'select' && !editorState.selectedEntity && layerState.showRegions))) {
      postMsg({
        type: 'region_delete_request',
        creationNumber: regionState.selectedNumber
      });
    } else if (e.key === 'Escape' && (editorState.mode === 'place_unit' || editorState.mode === 'place_doodad' || editorState.mode === 'place_region')) {
      editorState.mode = 'select';
      editorState.placeSpec = null;
      regionState.dragMode = null;
      regionState.createDraft = null;
      canvas.style.cursor = 'default';
      postMsg({ type: 'placement_cancelled' });
    }
  });

  window.addEventListener('mousemove', (e) => {
    if (regionState.dragMode && !cameraState.isRotating && !cameraState.isPanning) {
      let hit = raycastTerrain(e.clientX, e.clientY);
      if (hit) {
        if (regionState.dragMode === 'create' && regionState.createDraft) {
          regionState.createDraft.left = Math.min(regionState.dragStartWx, hit[0]);
          regionState.createDraft.right = Math.max(regionState.dragStartWx, hit[0]);
          regionState.createDraft.bottom = Math.min(regionState.dragStartWy, hit[1]);
          regionState.createDraft.top = Math.max(regionState.dragStartWy, hit[1]);
        } else if (regionState.origRect && regionState.selectedNumber !== null) {
          let reg = regionState.regions.find(r => r.creationNumber === regionState.selectedNumber);
          if (reg) {
            editorState.dragMoved = true;
            let o = regionState.origRect;
            let dx = hit[0] - regionState.dragStartWx;
            let dy = hit[1] - regionState.dragStartWy;
            if (regionState.dragMode === 'move') {
              reg.left = o.left + dx;
              reg.right = o.right + dx;
              reg.bottom = o.bottom + dy;
              reg.top = o.top + dy;
            } else {
              let l = o.left, r = o.right, b = o.bottom, t = o.top;
              if (regionState.dragMode.includes('w')) l = Math.min(o.right - 32, hit[0]);
              if (regionState.dragMode.includes('e')) r = Math.max(o.left + 32, hit[0]);
              if (regionState.dragMode.includes('s')) b = Math.min(o.top - 32, hit[1]);
              if (regionState.dragMode.includes('n')) t = Math.max(o.bottom + 32, hit[1]);
              reg.left = l; reg.right = r; reg.bottom = b; reg.top = t;
            }
          }
        }
      }
      return;
    }

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
        if (editorState.placeSpec && viewer && viewer.map && typeof viewer.map.evaluateEntityPlacement === 'function') {
          let evalRes = viewer.map.evaluateEntityPlacement(
            editorState.placeSpec.kind,
            editorState.placeSpec.rawcode,
            null,
            hit[0],
            hit[1],
            editorState.ignorePathing
          );
          editorState.placementEval = evalRes;
          editorState.hoverWx = evalRes.x;
          editorState.hoverWy = evalRes.y;
        } else {
          editorState.placementEval = null;
          editorState.hoverWx = hit[0];
          editorState.hoverWy = hit[1];
        }
      }
    } else if (editorState.isDraggingEntity && editorState.selectedEntity) {
      if (Math.hypot(e.clientX - cameraState.downX, e.clientY - cameraState.downY) >= 4) {
        let hit = raycastTerrain(e.clientX, e.clientY);
        if (hit && viewer && viewer.map) {
          editorState.dragMoved = true;
          let tx = hit[0], ty = hit[1], tz = hit[2];
          if (typeof viewer.map.evaluateEntityPlacement === 'function') {
            let evalRes = viewer.map.evaluateEntityPlacement(
              editorState.selectedEntity.kind,
              editorState.selectedEntity.id,
              editorState.selectedEntity.creationNumber,
              hit[0],
              hit[1],
              editorState.ignorePathing
            );
            editorState.placementEval = evalRes;
            tx = evalRes.x;
            ty = evalRes.y;
            tz = evalRes.z;
            if (evalRes.valid || editorState.ignorePathing) {
              editorState.dragLastValidPos = { x: tx, y: ty, z: tz };
            }
          }
          editorState.selectedEntity.x = tx;
          editorState.selectedEntity.y = ty;
          editorState.selectedEntity.z = tz;
          if (editorState.selectedEntity.kind === 'unit' && typeof viewer.map.updateLiveUnit === 'function') {
            viewer.map.updateLiveUnit(editorState.selectedEntity.creationNumber, { x: tx, y: ty, z: tz });
          } else if (editorState.selectedEntity.kind === 'doodad' && typeof viewer.map.updateLiveDoodad === 'function') {
            viewer.map.updateLiveDoodad(editorState.selectedEntity.creationNumber, { x: tx, y: ty, z: tz });
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
    if (loadingOverlay) loadingOverlay.style.display = 'none';
    if (loadingSpinner) loadingSpinner.textContent = 'Cargando Terreno y Modelos 3D...';
    if (loadingBarFill) loadingBarFill.style.width = '30%';
    if (loadingDetails) loadingDetails.textContent = 'Descargando datos del mapa...';
    postMsg({ type: 'map_load_progress', percent: 82, text: 'Cargando datos MPQ en el motor 3D...' });

    fetch(url)
      .then((res) => {
        if (!res.ok) throw new Error('HTTP ' + res.status);
        return res.arrayBuffer();
      })
      .then((buffer) => {
        if (!buffer || buffer.byteLength === 0) {
          clearMap();
          postMsg({ type: 'map_loaded_success' });
          return;
        }

        if (loadingBarFill) loadingBarFill.style.width = '68%';
        if (loadingDetails) loadingDetails.textContent = 'Procesando terreno, acantilados y doodads...';
        postMsg({ type: 'map_load_progress', percent: 90, text: 'Construyendo mallas de terreno, acantilados, árboles y unidades...' });

        // Ceder un frame para que el diálogo de progreso se pinte antes del parseo síncrono
        requestAnimationFrame(() => {
          try {
            viewer.loadMap(buffer);
            applyLayerVisibility();
            centerTargetOnMap();
            resizeCanvas();
            updateCamera();
            if (loadingBarFill) loadingBarFill.style.width = '100%';
            if (loadingOverlay) loadingOverlay.style.display = 'none';
            if (noMapHint) noMapHint.style.display = 'none';

            postMsg({ type: 'map_load_progress', percent: 100, text: 'Mapa listo.' });
            postMsg({ type: 'map_loaded_success' });
          } catch (innerErr) {
            console.error('Error al procesar mapa 3D:', innerErr);
            postMsg({ type: 'map_load_error', error: innerErr && innerErr.message ? innerErr.message : String(innerErr) });
          }
        });
      })
      .catch((err) => {
        console.error('Error al cargar mapa:', err);
        if (loadingDetails) loadingDetails.textContent = 'Error cargando mapa: ' + err.message;
        postMsg({ type: 'map_load_error', error: err && err.message ? err.message : String(err) });
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
      } else if (data.action === 'set_lighting') {
        if (typeof window.setW3LightingPreset === 'function') {
          window.setW3LightingPreset(data.preset);
        }
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
        editorState.placementEval = null;
        brushState.active = editorState.mode === 'terrain';
        canvas.style.cursor = (editorState.mode === 'select') ? 'default' : 'crosshair';
        if (editorState.mode !== 'place_unit' && editorState.mode !== 'place_doodad') {
          editorState.hoverWx = null;
          editorState.hoverWy = null;
        } else if (editorState.placeSpec && viewer && viewer.map && typeof viewer.map.getEntityPathingSpec === 'function') {
          viewer.map.getEntityPathingSpec(editorState.placeSpec.kind, editorState.placeSpec.rawcode);
        }
      } else if (data.action === 'set_pathing_options') {
        editorState.ignorePathing = !!data.ignorePathing;
      } else if (data.action === 'set_object_overrides') {
        window._w3sObjectOverrides = data.overrides || null;
        if (viewer) viewer._w3sObjectOverrides = window._w3sObjectOverrides;
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
          applyLayerVisibility();
        }
      } else if (data.action === 'set_layer_visibility') {
        if (typeof data.showUnits === 'boolean') layerState.showUnits = data.showUnits;
        if (typeof data.showDoodads === 'boolean') layerState.showDoodads = data.showDoodads;
        if (typeof data.showRegions === 'boolean') layerState.showRegions = data.showRegions;
        if (typeof data.showGrid === 'boolean') layerState.showGrid = data.showGrid;
        if (typeof data.showPathing === 'boolean') layerState.showPathing = data.showPathing;
        if (typeof data.showBounds === 'boolean') layerState.showBounds = data.showBounds;
        if (typeof data.showWeather === 'boolean') layerState.showWeather = data.showWeather;
        if (typeof data.weatherOverride === 'string') layerState.weatherOverride = data.weatherOverride;
        applyLayerVisibility();
      } else if (data.action === 'set_regions' && Array.isArray(data.regions)) {
        regionState.regions = data.regions;
      } else if (data.action === 'select_region') {
        regionState.selectedNumber = typeof data.creationNumber === 'number' && data.creationNumber >= 0
          ? data.creationNumber
          : null;
        if (data.focus && regionState.selectedNumber !== null) {
          let reg = regionState.regions.find(r => r.creationNumber === regionState.selectedNumber);
          if (reg) {
            let cx = (reg.left + reg.right) * 0.5;
            let cy = (reg.bottom + reg.top) * 0.5;
            let hz = (viewer && viewer.map && typeof viewer.map.heightAt === 'function')
              ? viewer.map.heightAt(cx, cy)
              : 0;
            cameraState.target = [cx, cy, hz];
            if (typeof data.distance === 'number') cameraState.distance = data.distance;
            updateCamera();
          }
        }
      } else if (data.action === 'focus_camera_preset' && data.camera) {
        let c = data.camera;
        let hz = (viewer && viewer.map && typeof viewer.map.heightAt === 'function')
          ? viewer.map.heightAt(c.targetX, c.targetY)
          : 0;
        cameraState.target = [c.targetX, c.targetY, hz + (c.zOffset || 0)];
        cameraState.distance = Math.max(300, Math.min(18000, c.distance || 1650));
        let rot = typeof c.rotation === 'number' ? c.rotation : 90;
        cameraState.horizontalAngle = ((rot - 90) * Math.PI) / 180;
        let aoa = typeof c.angleOfAttack === 'number' ? c.angleOfAttack : 304;
        if (aoa < 180) aoa += 360;
        cameraState.verticalAngle = Math.min(Math.max(0.08, ((aoa - 270) * Math.PI) / 180), Math.PI / 2 - 0.04);
        updateCamera();
      } else if (data.action === 'request_camera_state') {
        let hz = (viewer && viewer.map && typeof viewer.map.heightAt === 'function')
          ? viewer.map.heightAt(cameraState.target[0], cameraState.target[1])
          : 0;
        let rot = 90 + (cameraState.horizontalAngle * 180) / Math.PI;
        rot = ((rot % 360) + 360) % 360;
        let aoa = 270 + (cameraState.verticalAngle * 180) / Math.PI;
        postMsg({
          type: 'camera_state_captured',
          requestId: data.requestId || '',
          targetX: cameraState.target[0],
          targetY: cameraState.target[1],
          zOffset: cameraState.target[2] - hz,
          rotation: rot,
          angleOfAttack: aoa,
          distance: cameraState.distance
        });
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

  window.__w3sGetVisualState = () => ({
    layerState: Object.assign({}, layerState),
    hasPathing: !!(viewer && viewer.map && viewer.map._w3sPathing),
    pathingSize: viewer && viewer.map && viewer.map._w3sPathing
      ? [viewer.map._w3sPathing.width, viewer.map._w3sPathing.height]
      : null,
    visuals: viewer && viewer.map ? viewer.map._w3sVisuals : null
  });

  // Render loop
  function step(nowMs) {
    requestAnimationFrame(step);
    viewer.updateAndRender();
    drawViewportOverlays(nowMs);
  }
  requestAnimationFrame(step);

})();
