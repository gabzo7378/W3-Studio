// W3 Studio 3D Model Viewer Engine (Powered by GhostWolf's mdx-m3-viewer & WebGL)
(function () {
  'use strict';

  const MV = window.ModelViewer;
  if (!MV) {
    console.error('ModelViewer library could not be loaded.');
    return;
  }

  const canvas = document.getElementById('viewer-canvas');
  const animSelect = document.getElementById('anim-select');
  const teamSelect = document.getElementById('team-select');
  const btnResetCam = document.getElementById('btn-reset-cam');
  const modelBadge = document.getElementById('model-badge');
  const loadingIndicator = document.getElementById('loading-indicator');

  // Path solver routing requests to our local MPQ bridge in C#
  function pathSolver(src) {
    if (!src) return '';
    let clean = src.replace(/\\/g, '/').replace(/^\/+/, '');
    return 'https://w3studio.local/mpq/' + clean;
  }

  // Create the WebGL ModelViewer
  const viewer = new MV.viewer.ModelViewer(canvas);
  const scene = viewer.addScene();

  // Register MDX and BLP handlers
  viewer.addHandler(MV.viewer.handlers.mdx, pathSolver);
  viewer.addHandler(MV.viewer.handlers.blp);

  // Camera Orbit State
  const cameraState = {
    horizontalAngle: Math.PI / 4,
    verticalAngle: Math.PI / 3,
    distance: 260,
    target: [0, 0, 45],
    initialDistance: 260,
    initialTarget: [0, 0, 45],
    isRotating: false,
    isPanning: false,
    lastX: 0,
    lastY: 0
  };

  function updateCamera() {
    let phi = Math.min(Math.max(0.04, cameraState.verticalAngle), Math.PI - 0.04);
    cameraState.verticalAngle = phi;
    let theta = cameraState.horizontalAngle;
    let d = cameraState.distance;

    let x = d * Math.sin(phi) * Math.sin(theta) + cameraState.target[0];
    let y = -d * Math.sin(phi) * Math.cos(theta) + cameraState.target[1];
    let z = d * Math.cos(phi) + cameraState.target[2];

    scene.camera.moveToAndFace([x, y, z], cameraState.target, [0, 0, 1]);
  }

  function resizeCanvas() {
    let w = Math.max(canvas.parentElement.clientWidth || 300, 50);
    let h = Math.max(canvas.parentElement.clientHeight || 300, 50);
    if (canvas.width !== w || canvas.height !== h) {
      canvas.width = w;
      canvas.height = h;
      scene.viewport[2] = w;
      scene.viewport[3] = h;
      scene.camera.perspective(Math.PI / 4, w / h, 2, 20000);
      updateCamera();
    }
  }
  window.addEventListener('resize', resizeCanvas);
  setTimeout(resizeCanvas, 50);

  // Mouse camera interactions
  canvas.addEventListener('contextmenu', (e) => e.preventDefault());

  canvas.addEventListener('mousedown', (e) => {
    e.preventDefault();
    cameraState.lastX = e.clientX;
    cameraState.lastY = e.clientY;
    if (e.button === 0) cameraState.isRotating = true;
    if (e.button === 1 || e.button === 2) cameraState.isPanning = true;
  });

  window.addEventListener('mouseup', () => {
    cameraState.isRotating = false;
    cameraState.isPanning = false;
  });

  window.addEventListener('mousemove', (e) => {
    if (!cameraState.isRotating && !cameraState.isPanning) return;

    let dx = e.clientX - cameraState.lastX;
    let dy = e.clientY - cameraState.lastY;
    cameraState.lastX = e.clientX;
    cameraState.lastY = e.clientY;

    if (cameraState.isRotating) {
      cameraState.horizontalAngle -= dx * 0.01;
      cameraState.verticalAngle -= dy * 0.01;
      updateCamera();
    } else if (cameraState.isPanning) {
      let dirX = scene.camera.directionX;
      let dirY = scene.camera.directionY;
      let w = Math.max(canvas.width, 1);
      let h = Math.max(canvas.height, 1);
      let aspect = w / h;
      let sw = (-dx / w) * cameraState.distance * aspect * 0.9;
      let sh = (dy / h) * cameraState.distance * 0.9;

      cameraState.target[0] += dirX[0] * sw + dirY[0] * sh;
      cameraState.target[1] += dirX[1] * sw + dirY[1] * sh;
      cameraState.target[2] += dirX[2] * sw + dirY[2] * sh;
      updateCamera();
    }
  });

  canvas.addEventListener('wheel', (e) => {
    e.preventDefault();
    let delta = e.deltaY > 0 ? 1 : -1;
    cameraState.distance = Math.max(15, Math.min(8000, cameraState.distance * (1 + delta * 0.12)));
    updateCamera();
  });

  btnResetCam.addEventListener('click', () => {
    cameraState.horizontalAngle = Math.PI / 4;
    cameraState.verticalAngle = Math.PI / 3;
    cameraState.distance = cameraState.initialDistance;
    cameraState.target = [...cameraState.initialTarget];
    updateCamera();
  });

  // State of currently loaded model
  let currentModel = null;
  let currentInstance = null;
  let currentModelPath = '';

  function showLoading(show) {
    loadingIndicator.style.display = show ? 'block' : 'none';
  }

  function populateAnimations(model) {
    animSelect.innerHTML = '';
    if (!model || !model.sequences || model.sequences.length === 0) {
      let opt = document.createElement('option');
      opt.value = '-1';
      opt.textContent = 'Sin animación';
      animSelect.appendChild(opt);
      return;
    }

    let defaultIdx = 0;
    let birthIdx = -1;
    model.sequences.forEach((seq, idx) => {
      let opt = document.createElement('option');
      opt.value = idx.toString();
      opt.textContent = `${idx}: ${seq.name}`;
      animSelect.appendChild(opt);

      // Prefer "Stand", then "Birth" (common for spell effects) as default animation
      let lower = seq.name.toLowerCase();
      if (lower.startsWith('stand') && defaultIdx === 0) {
        defaultIdx = idx;
      } else if (lower.startsWith('birth') && birthIdx === -1) {
        birthIdx = idx;
      }
    });
    if (defaultIdx === 0 && birthIdx !== -1 && !model.sequences[0].name.toLowerCase().startsWith('stand')) {
      defaultIdx = birthIdx;
    }

    animSelect.value = defaultIdx.toString();
    if (currentInstance) {
      currentInstance.setSequence(defaultIdx);
    }
  }

  animSelect.addEventListener('change', () => {
    let idx = parseInt(animSelect.value, 10);
    if (currentInstance) {
      currentInstance.setSequence(idx);
    }
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage({
        type: 'animation_changed',
        index: idx
      });
    }
  });

  teamSelect.addEventListener('change', () => {
    let team = parseInt(teamSelect.value, 10);
    if (currentInstance) {
      currentInstance.setTeamColor(team);
    }
  });

  function clearModel() {
    currentModelPath = '';
    if (currentInstance) {
      try { scene.removeInstance(currentInstance); } catch (e) {}
      currentInstance = null;
    }
    currentModel = null;
    if (modelBadge) {
      modelBadge.textContent = 'Sin modelo';
      modelBadge.title = '';
    }
    if (animSelect) {
      animSelect.innerHTML = '';
    }
    showLoading(false);
  }

  function loadModel(mdxPath, teamColor = 0) {
    if (!mdxPath) {
      clearModel();
      return;
    }

    if (currentInstance) {
      try { scene.removeInstance(currentInstance); } catch (e) {}
      currentInstance = null;
    }
    currentModel = null;

    currentModelPath = mdxPath;
    let baseName = mdxPath.split('\\').pop().split('/').pop();
    modelBadge.textContent = baseName;
    modelBadge.title = mdxPath;
    showLoading(true);

    let promise = viewer.load(mdxPath, pathSolver);
    if (!promise) {
      clearModel();
      return;
    }

    promise.then((model) => {
      showLoading(false);
      if (currentModelPath !== mdxPath) return;
      if (!model) {
        console.warn('Model could not be loaded:', mdxPath);
        clearModel();
        return;
      }

      currentModel = model;
      currentInstance = model.addInstance();
      currentInstance.setScene(scene);
      currentInstance.setTeamColor(teamColor);
      currentInstance.setSequenceLoopMode(2); // Loop continuously

      // Calculate bounding radius to center camera cleanly (supports units & particle spell effects)
      let bounds = model.bounds;
      if (bounds && bounds.radius > 12) {
        cameraState.distance = Math.max(90, bounds.radius * 2.6);
        cameraState.target = [bounds.center[0] || 0, bounds.center[1] || 0, bounds.center[2] || 35];
      } else {
        cameraState.distance = 220;
        cameraState.target = [0, 0, 35];
      }
      cameraState.initialDistance = cameraState.distance;
      cameraState.initialTarget = [...cameraState.target];
      updateCamera();

      populateAnimations(model);

      if (window.chrome && window.chrome.webview) {
        let seqNames = (model.sequences || []).map(s => s.name);
        window.chrome.webview.postMessage({
          type: 'model_loaded',
          path: mdxPath,
          sequences: seqNames
        });
      }
    }).catch((err) => {
      clearModel();
      console.error('Failed to load model:', err);
    });
  }

  // Listen to C# WebView2 host messages
  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener('message', (event) => {
      let data = event.data;
      if (!data) return;

      if (data.action === 'load_model') {
        loadModel(data.path, data.teamColor !== undefined ? data.teamColor : 0);
      } else if (data.action === 'clear_model') {
        clearModel();
      } else if (data.action === 'set_animation') {
        let idx = data.index;
        if (animSelect) animSelect.value = idx.toString();
        if (currentInstance) currentInstance.setSequence(idx);
      } else if (data.action === 'set_team_color') {
        let color = data.color;
        if (teamSelect) teamSelect.value = color.toString();
        if (currentInstance) currentInstance.setTeamColor(color);
      } else if (data.action === 'reset_camera') {
        btnResetCam.click();
      }
    });

    // Notify C# host that viewer is initialized and ready
    window.chrome.webview.postMessage({ type: 'ready' });
  }

  // Render loop
  function step() {
    requestAnimationFrame(step);
    viewer.updateAndRender();
  }
  requestAnimationFrame(step);

})();

