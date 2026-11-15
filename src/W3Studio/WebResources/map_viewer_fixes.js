// map_viewer_fixes.js — Warcraft III 1.27b Faithful Renderer & Edge-Case Fixes
// Loaded after viewer.min.js and before map_viewer.js.
(function () {
  "use strict";

  const MV = window.ModelViewer;
  if (!MV || !MV.viewer || !MV.viewer.handlers) {
    console.warn("[W3Studio Fixes] ModelViewer.viewer.handlers not found.");
    return;
  }

  const handlers = MV.viewer.handlers;
  const War3MapViewer = handlers.War3MapViewer;
  const War3Map = handlers.War3Map;

  if (!War3MapViewer || !War3Map) {
    console.warn("[W3Studio Fixes] War3MapViewer or War3Map missing; skipping patches.");
    return;
  }

  const GL_PREC =
    "\n#ifdef GL_FRAGMENT_PRECISION_HIGH\nprecision highp float;\n#else\nprecision mediump float;\n#endif\n";

  // =========================================================================
  // 0. Helpers: War3MapViewer.w3i, DNC MDX parser, TerrainLights INI, UberSplats
  // =========================================================================
  const SHORE_DEPTH = 24.5;
  const MAP_FLAG_CLIFF_SHORES = 0x0800;
  const MAP_FLAG_ROLLING_SHORES = 0x1000;
  const MAP_FLAG_TERRAIN_FOG = 0x2000;
  const MAP_FLAG_WATER_TINTING = 0x10000;
  const PRESET_HOURS = { dawn: 6, noon: 12, dusk: 18, midnight: 0 };

  function parseIniSection(text, sectionName) {
    const out = new Map();
    if (!text) return out;
    const targetHeader = `[${sectionName.toLowerCase()}]`;
    let inSection = false;
    for (const rawLine of text.split(/\r?\n/)) {
      const line = rawLine.trim();
      if (!line || line.startsWith("//") || line.startsWith(";")) continue;
      if (line.startsWith("[")) {
        inSection = line.toLowerCase() === targetHeader;
        continue;
      }
      if (!inSection) continue;
      const eq = line.indexOf("=");
      if (eq > 0) {
        out.set(line.slice(0, eq).trim().toUpperCase(), line.slice(eq + 1).trim());
      }
    }
    return out;
  }

  function parseW3iVisuals(bytes) {
    if (!bytes || bytes.length < 64) return null;
    try {
      const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
      let p = 0;
      const i32 = () => { const v = dv.getInt32(p, true); p += 4; return v; };
      const u32 = () => { const v = dv.getUint32(p, true); p += 4; return v; };
      const f32 = () => { const v = dv.getFloat32(p, true); p += 4; return v; };
      const cstr = () => {
        const start = p;
        while (p < bytes.length && bytes[p] !== 0) p++;
        p++;
        return new TextDecoder().decode(bytes.subarray(start, p - 1));
      };

      const version = i32();
      i32(); // saves
      i32(); // editorVersion
      if (version > 27) p += 16; // buildVersion[4]
      cstr(); cstr(); cstr(); cstr(); // name, author, description, recommendedPlayers
      let cameraBounds = null;
      if (p + 32 <= bytes.length) {
        const vals = [f32(), f32(), f32(), f32(), f32(), f32(), f32(), f32()];
        const xs = [vals[0], vals[2], vals[4], vals[6]].filter(Number.isFinite);
        const ys = [vals[1], vals[3], vals[5], vals[7]].filter(Number.isFinite);
        if (xs.length === 4 && ys.length === 4) {
          const minX = Math.min(...xs), maxX = Math.max(...xs);
          const minY = Math.min(...ys), maxY = Math.max(...ys);
          if (maxX > minX && maxY > minY) {
            cameraBounds = { minX, minY, maxX, maxY };
          }
        }
      } else {
        p += 32;
      }
      const complements = p + 16 <= bytes.length
        ? [i32(), i32(), i32(), i32()]
        : [0, 0, 0, 0];
      const playableSize = p + 8 <= bytes.length
        ? [i32(), i32()]
        : [0, 0];
      const flags = u32();
      const tileset = String.fromCharCode(bytes[p++]);
      i32(); // campaignBackground
      if (version > 24) cstr(); // loadingScreenModel
      cstr(); cstr(); cstr(); // loadingScreenText, Title, Subtitle
      i32(); // loadingScreen
      if (version > 24) cstr(); // prologueScreenModel
      cstr(); cstr(); cstr(); // prologueScreenText, Title, Subtitle

      let fog = null;
      let lightEnv = tileset;
      let waterTint = [1, 1, 1, 1];
      let globalWeather = "";
      if (version > 24 && p + 25 <= bytes.length) {
        const style = i32();
        const start = f32();
        const end = f32();
        const density = f32();
        const fb = bytes[p++], fg = bytes[p++], fr = bytes[p++], fa = bytes[p++];
        if (flags & MAP_FLAG_TERRAIN_FOG) {
          fog = { style, start, end, density, color: [fr / 255, fg / 255, fb / 255] };
        }
        const weatherCode = i32();
        if (weatherCode !== 0) {
          const wChars = String.fromCharCode(
            weatherCode & 0xff,
            (weatherCode >> 8) & 0xff,
            (weatherCode >> 16) & 0xff,
            (weatherCode >> 24) & 0xff
          );
          if (/^[A-Za-z0-9]{4}$/.test(wChars)) {
            globalWeather = wChars;
          }
        }
        cstr(); // soundEnvironment
        const leChar = String.fromCharCode(bytes[p++]);
        if (/^[A-Za-z]$/.test(leChar)) lightEnv = leChar;
        const wb = bytes[p++], wg = bytes[p++], wr = bytes[p++], wa = bytes[p++];
        if (flags & MAP_FLAG_WATER_TINTING) {
          waterTint = [wr / 255, wg / 255, wb / 255, wa / 255];
        }
      }
      return {
        flags,
        tileset: tileset.toUpperCase(),
        lightEnv: lightEnv.toUpperCase(),
        fog,
        waterTint,
        globalWeather,
        cameraBounds,
        complements,
        playableSize,
        cliffShores: (flags & MAP_FLAG_CLIFF_SHORES) !== 0,
        rollingShores: (flags & MAP_FLAG_ROLLING_SHORES) !== 0
      };
    } catch (err) {
      console.debug("[W3Studio Fixes] war3map.w3i visuals read skipped:", err);
      return null;
    }
  }

  function parseWpmPathing(bytes) {
    try {
      if (!bytes || bytes.length < 16) return null;
      if (bytes[0] !== 0x4d || bytes[1] !== 0x50 || bytes[2] !== 0x33 || bytes[3] !== 0x57) return null; // 'MP3W'
      const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
      const width = dv.getInt32(8, true);
      const height = dv.getInt32(12, true);
      if (width <= 0 || height <= 0 || bytes.length < 16 + width * height) return null;
      return {
        width,
        height,
        cells: bytes.slice(16, 16 + width * height)
      };
    } catch {
      return null;
    }
  }

  function applyBoundaryToShadowMap(map) {
    try {
      if (!map || !map.viewer || !map.viewer.gl || !map.corners || !map.columns || !map.rows) return;
      const cols = map.columns | 0;
      const rows = map.rows | 0;
      if (cols <= 0 || rows <= 0) return;

      let hasBoundary = false;
      for (let y = 0; y <= rows && !hasBoundary; y++) {
        const row = map.corners[y];
        if (!row) continue;
        for (let x = 0; x <= cols; x++) {
          const c = row[x];
          if (c && (c.boundary || c.mapEdge)) {
            hasBoundary = true;
            break;
          }
        }
      }
      if (!hasBoundary) return;

      const shdW = cols * 4;
      const shdH = rows * 4;
      let buf = null;

      const shdFile = map.map && map.map.get("war3map.shd");
      if (shdFile) {
        try {
          const sb = shdFile.bytes();
          if (sb && sb.length >= shdW * shdH) {
            buf = typeof map.softenShadowMap === "function"
              ? new Uint8Array(map.softenShadowMap(sb, shdW, shdH))
              : new Uint8Array(sb.subarray(0, shdW * shdH));
          }
        } catch {
          buf = null;
        }
      }
      if (!buf) {
        buf = new Uint8Array(shdW * shdH);
      }

      for (let y = 0; y < rows; y++) {
        const row0 = map.corners[y];
        const row1 = map.corners[y + 1];
        if (!row0 || !row1) continue;
        for (let x = 0; x < cols; x++) {
          const c00 = row0[x], c10 = row0[x + 1], c01 = row1[x], c11 = row1[x + 1];
          const b00 = c00 && (c00.boundary || c00.mapEdge);
          const b10 = c10 && (c10.boundary || c10.mapEdge);
          const b01 = c01 && (c01.boundary || c01.mapEdge);
          const b11 = c11 && (c11.boundary || c11.mapEdge);
          if (!b00 && !b10 && !b01 && !b11) continue;

          for (let sy = 0; sy < 4; sy++) {
            const fy = sy >= 2;
            for (let sx = 0; sx < 4; sx++) {
              const fx = sx >= 2;
              const cornerFlag = fy ? (fx ? b11 : b01) : (fx ? b10 : b00);
              if (cornerFlag) {
                const idx = (y * 4 + sy) * shdW + (x * 4 + sx);
                if (buf[idx] < 228) buf[idx] = 228;
              }
            }
          }
        }
      }

      const gl = map.viewer.gl;
      if (!map.shadowMap) {
        map.shadowMap = gl.createTexture();
      }
      gl.bindTexture(gl.TEXTURE_2D, map.shadowMap);
      gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.ALPHA, shdW, shdH, 0, gl.ALPHA, gl.UNSIGNED_BYTE, buf);
    } catch (err) {
      console.debug("[W3Studio Fixes] Boundary shadow mask skipped:", err);
    }
  }

  function readLightMdx(buf) {
    if (!buf || buf.length < 12) return null;
    const dv = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
    let p = 0;
    const tagAt = (off) => String.fromCharCode(buf[off], buf[off + 1], buf[off + 2], buf[off + 3]);
    if (tagAt(0) !== "MDLX") return null;
    p = 4;
    const u32 = () => { const v = dv.getUint32(p, true); p += 4; return v; };
    const i32 = () => { const v = dv.getInt32(p, true); p += 4; return v; };
    const f32 = () => { const v = dv.getFloat32(p, true); p += 4; return v; };
    const floats = (n) => { const a = []; for (let i = 0; i < n; i++) a.push(f32()); return a; };
    const readTrack = (width) => {
      p += 4;
      const n = u32(), interp = u32(), globalSeq = i32();
      const keys = [];
      for (let i = 0; i < n; i++) {
        const k = { time: i32(), value: floats(width) };
        if (interp > 1) { k.inTan = floats(width); k.outTan = floats(width); }
        keys.push(k);
      }
      return { interp, globalSeq, keys };
    };
    const NODE_TRACKS = { KGTR: ["translation", 3], KGRT: ["rotation", 4], KGSC: ["scaling", 3] };
    const LIGHT_TRACKS = { KLAS: ["attenuationStart", 1], KLAE: ["attenuationEnd", 1], KLAC: ["color", 3], KLAI: ["intensity", 1], KLBC: ["ambColor", 3], KLBI: ["ambIntensity", 1], KLAV: ["visibility", 1] };

    const model = { version: 800, sequences: [], lights: [] };
    while (p + 8 <= buf.length) {
      const tag = tagAt(p); p += 4;
      const chunkSize = u32();
      const end = Math.min(buf.length, p + chunkSize);
      if (tag === "VERS") {
        model.version = u32();
      } else if (tag === "SEQS") {
        while (p + 132 <= end) {
          p += 80;
          const start = u32(), stop = u32();
          p += 44; // moveSpeed, flags, rarity, syncPoint, bounds(28)
          model.sequences.push({ start, end: stop });
        }
      } else if (tag === "LITE") {
        while (p + 8 <= end) {
          const lightStart = p;
          const lightEnd = Math.min(end, lightStart + u32());
          const nodeStart = p;
          const nodeEnd = Math.min(lightEnd, nodeStart + u32());
          p += 80 + 12; // name, objectId, parentId, flags
          const node = {};
          while (p + 4 <= nodeEnd) {
            const spec = NODE_TRACKS[tagAt(p)];
            if (!spec) break;
            node[spec[0]] = readTrack(spec[1]);
          }
          p = nodeEnd;
          const type = u32();
          if (model.version > 800) u32(); // Reforged unknown u32
          const attStart = f32(), attEnd = f32();
          const color = floats(3), intensity = f32();
          const ambColor = floats(3), ambIntensity = f32();
          if (model.version > 800) p += 24; // Reforged shadow/falloff fields
          const light = {
            type,
            attenuationStart: { value: [attStart] },
            attenuationEnd: { value: [attEnd] },
            color: { value: color },
            intensity: { value: [intensity] },
            ambColor: { value: ambColor },
            ambIntensity: { value: [ambIntensity] },
            ...node
          };
          while (p + 4 <= lightEnd) {
            const spec = LIGHT_TRACKS[tagAt(p)];
            if (!spec) break;
            const tr = readTrack(spec[1]);
            if (spec[0] === "visibility") light.visibility = tr;
            else light[spec[0]] = { track: tr };
          }
          model.lights.push(light);
          p = lightEnd;
        }
      }
      p = end;
    }
    return model;
  }

  function sampleTrack(track, time) {
    const keys = track.keys;
    if (!keys || keys.length === 0) return [];
    if (time <= keys[0].time) return keys[0].value;
    const last = keys[keys.length - 1];
    if (time >= last.time) return last.value;
    let i = 0;
    while (i < keys.length - 2 && keys[i + 1].time <= time) i++;
    const a = keys[i], b = keys[i + 1];
    if (track.interp === 0 || b.time === a.time) return a.value;
    const u = (time - a.time) / (b.time - a.time);
    if (track.interp === 1 || !a.outTan || !b.inTan) {
      return a.value.map((v, k) => v + (b.value[k] - v) * u);
    }
    if (track.interp === 2) {
      const u2 = u * u, u3 = u2 * u;
      const h1 = 2 * u3 - 3 * u2 + 1, h2 = -2 * u3 + 3 * u2, h3 = u3 - 2 * u2 + u, h4 = u3 - u2;
      return a.value.map((v, k) => h1 * v + h2 * b.value[k] + h3 * a.outTan[k] + h4 * b.inTan[k]);
    }
    const iu = 1 - u;
    return a.value.map((v, k) => iu * iu * iu * v + 3 * iu * iu * u * a.outTan[k] + 3 * iu * u * u * b.inTan[k] + u * u * u * b.value[k]);
  }

  function evaluateDncLight(model, hour) {
    if (!model || !model.lights || model.lights.length === 0) return null;
    const l = model.lights.find((x) => x.type === 1) || model.lights[0];
    const seq = model.sequences[0] || { start: 0, end: 60000 };
    const hNorm = (((hour % 24) + 24) % 24) / 24;
    const time = seq.start + hNorm * (seq.end - seq.start);
    const propAt = (p) => p.track ? sampleTrack(p.track, p.track.globalSeq >= 0 ? 0 : time) : p.value;
    const q = l.rotation ? sampleTrack(l.rotation, l.rotation.globalSeq >= 0 ? 0 : time) : [0, 0, 0, 1];
    const [x, y, z, w] = q.length === 4 ? q : [0, 0, 0, 1];
    const dir = [2 * (x * z + w * y), 2 * (y * z - w * x), 1 - 2 * (x * x + y * y)];
    const intensity = propAt(l.intensity)[0] ?? 1;
    const ambIntensity = propAt(l.ambIntensity)[0] ?? 0;
    const bgr = (c, k) => [(c[2] ?? 1) * k, (c[1] ?? 1) * k, (c[0] ?? 1) * k];
    return {
      dir,
      diffuse: bgr(propAt(l.color), intensity),
      ambient: bgr(propAt(l.ambColor), ambIntensity),
      levels: [ambIntensity, intensity]
    };
  }

  function setFogUniforms(gl, u, fog) {
    if (u.u_fogColor !== undefined) {
      if (fog) gl.uniform4f(u.u_fogColor, fog.color[0], fog.color[1], fog.color[2], 1);
      else gl.uniform4f(u.u_fogColor, 0, 0, 0, 0);
    }
    if (u.u_fogRange !== undefined) {
      gl.uniform3f(u.u_fogRange, fog ? fog.start : 0, fog ? fog.end : 0, fog ? fog.density : 0);
    }
    if (u.u_fogStyle !== undefined) {
      gl.uniform1f(u.u_fogStyle, fog ? fog.style : 0);
    }
  }

  function setLightingUniforms(gl, u, sceneLight) {
    if (u.u_hasDncLight !== undefined) {
      gl.uniform1f(u.u_hasDncLight, sceneLight ? 1 : 0);
    }
    if (sceneLight) {
      if (u.u_lightDir !== undefined) gl.uniform3fv(u.u_lightDir, sceneLight.dir);
      if (u.u_lightDiffuse !== undefined) gl.uniform3fv(u.u_lightDiffuse, sceneLight.diffuse);
      if (u.u_lightAmbient !== undefined) gl.uniform3fv(u.u_lightAmbient, sceneLight.ambient);
    }
  }

  window.setW3LightingPreset = function (preset) {
    const key = String(preset || "noon").trim().toLowerCase();
    const hour = Object.prototype.hasOwnProperty.call(PRESET_HOURS, key) ? PRESET_HOURS[key] : 12;
    const v = window.__w3sViewer;
    if (v) {
      v._w3sLightingHour = hour;
      v._w3sLightingPreset = key;
      if (v.map && v.map._w3sDncModel) {
        v.map._w3sSceneLight = evaluateDncLight(v.map._w3sDncModel, hour);
      }
    }
  };

  // =========================================================================
  // 1. Shader upgrades (WC3 1.27b DNC lighting, shared distance fog, >14 tiles, water, 8 cliffs, UberSplats)
  // Attribute names MUST remain identical so VAO bindings in viewer.min.js stay valid.
  // =========================================================================
  const FOG_FS_BLOCK = `
varying float v_fogDepth;
uniform vec4 u_fogColor;
uniform vec3 u_fogRange;
uniform float u_fogStyle;

vec3 applyW3Fog(vec3 rgb) {
  if (u_fogColor.a < 0.5) return rgb;
  float amt;
  if (u_fogStyle < 0.5) {
    amt = u_fogRange.y > u_fogRange.x
      ? clamp((v_fogDepth - u_fogRange.x) / (u_fogRange.y - u_fogRange.x), 0.0, 1.0)
      : (v_fogDepth >= u_fogRange.y ? 1.0 : 0.0);
  } else {
    float d = min(max(v_fogDepth, 0.0) * u_fogRange.z, 20.0);
    amt = clamp(1.0 - exp(u_fogStyle < 1.5 ? -d : -d * d), 0.0, 1.0);
  }
  return mix(rgb, u_fogColor.rgb, amt);
}
`;

  const DNC_LIGHT_FS_BLOCK = `
uniform vec3 u_lightDir;
uniform vec3 u_lightDiffuse;
uniform vec3 u_lightAmbient;
uniform float u_hasDncLight;
const vec3 defaultLightDirection = normalize(vec3(-0.35, -0.35, 0.5));

vec3 computeW3Lighting(vec3 normal) {
  vec3 n = normalize(normal);
  if (u_hasDncLight > 0.5) {
    vec3 dir = normalize(u_lightDir);
    float ndl = max(dot(n, dir), 0.0);
    float softWrap = clamp(dot(n, dir) * 0.5 + 0.5, 0.0, 1.0);
    float lit = mix(ndl, softWrap, 0.22);
    return max(u_lightAmbient + u_lightDiffuse * lit, vec3(0.05));
  }
  float halfLambert = clamp(dot(n, defaultLightDirection) * 0.5 + 0.5, 0.0, 1.0);
  float diffuse = smoothstep(0.08, 0.95, halfLambert);
  return vec3(0.56, 0.60, 0.66) + vec3(0.48, 0.47, 0.44) * diffuse;
}
`;

  const GROUND_VS = `
uniform mat4 u_VP;
uniform sampler2D u_heightMap;
uniform vec2 u_size;
uniform vec2 u_offset;
uniform bool u_extended[14];
uniform float u_baseTileset;

attribute vec2 a_position;
attribute float a_InstanceID;
attribute vec4 a_textures;
attribute vec4 a_variations;

varying vec4 v_tilesets;
varying vec2 v_uv[4];
varying vec3 v_normal;
varying vec2 v_mapUv;
varying float v_fogDepth;

vec2 getCell(float variation) {
  if (variation < 16.0) {
    return vec2(mod(variation, 4.0), floor(variation / 4.0));
  } else {
    variation -= 16.0;
    return vec2(4.0 + mod(variation, 4.0), floor(variation / 4.0));
  }
}

bool isExtendedSlot(int idx) {
  for (int k = 0; k < 14; k++) {
    if (k == idx) {
      return u_extended[k];
    }
  }
  return false;
}

vec2 getUV(vec2 position, bool extended, float variation) {
  vec2 cell = getCell(variation);
  vec2 cellSize = vec2(extended ? 0.125 : 0.25, 0.25);
  vec2 uv = vec2(position.x, 1.0 - position.y);
  vec2 pixelSize = vec2(1.0 / 512.0, 1.0 / 256.0);
  return clamp((cell + uv) * cellSize, cell * cellSize + pixelSize, (cell + 1.0) * cellSize - pixelSize);
}

void main() {
  vec4 textures = a_textures - u_baseTileset;

  float t0 = (textures[0] > 0.5 && textures[0] < 14.5) ? textures[0] : 0.0;
  float t1 = (textures[1] > 0.5 && textures[1] < 14.5) ? textures[1] : 0.0;
  float t2 = (textures[2] > 0.5 && textures[2] < 14.5) ? textures[2] : 0.0;
  float t3 = (textures[3] > 0.5 && textures[3] < 14.5) ? textures[3] : 0.0;

  if (t0 > 0.5 || t1 > 0.5 || t2 > 0.5 || t3 > 0.5) {
    v_tilesets = vec4(t0, t1, t2, t3);

    v_uv[0] = t0 > 0.5 ? getUV(a_position, isExtendedSlot(int(t0 - 0.6)), a_variations[0]) : vec2(0.0);
    v_uv[1] = t1 > 0.5 ? getUV(a_position, isExtendedSlot(int(t1 - 0.6)), a_variations[1]) : vec2(0.0);
    v_uv[2] = t2 > 0.5 ? getUV(a_position, isExtendedSlot(int(t2 - 0.6)), a_variations[2]) : vec2(0.0);
    v_uv[3] = t3 > 0.5 ? getUV(a_position, isExtendedSlot(int(t3 - 0.6)), a_variations[3]) : vec2(0.0);

    vec2 corner = vec2(mod(a_InstanceID, u_size.x), floor(a_InstanceID / u_size.x));
    vec2 base = corner + a_position;
    vec2 heightSize = u_size + vec2(1.0);
    float height = texture2D(u_heightMap, (base + vec2(0.5)) / heightSize).a;

    float hL = texture2D(u_heightMap, (base + vec2(-0.5, 0.5)) / heightSize).a;
    float hR = texture2D(u_heightMap, (base + vec2(1.5, 0.5)) / heightSize).a;
    float hD = texture2D(u_heightMap, (base + vec2(0.5, -0.5)) / heightSize).a;
    float hU = texture2D(u_heightMap, (base + vec2(0.5, 1.5)) / heightSize).a;

    v_normal = normalize(vec3(hL - hR, hD - hU, 2.0));
    v_mapUv = base / u_size;

    gl_Position = u_VP * vec4(base * 128.0 + u_offset, height * 128.0, 1.0);
    v_fogDepth = gl_Position.w;
  } else {
    v_tilesets = vec4(0.0);
    v_uv[0] = vec2(0.0);
    v_uv[1] = vec2(0.0);
    v_uv[2] = vec2(0.0);
    v_uv[3] = vec2(0.0);
    v_normal = vec3(0.0, 0.0, 1.0);
    v_mapUv = vec2(0.0);
    v_fogDepth = 0.0;
    gl_Position = vec4(0.0);
  }
}
`;

  const GROUND_FS = `${GL_PREC}
uniform sampler2D u_tilesets[14];
uniform sampler2D u_shadowMap;

varying vec4 v_tilesets;
varying vec2 v_uv[4];
varying vec3 v_normal;
varying vec2 v_mapUv;
${FOG_FS_BLOCK}
${DNC_LIGHT_FS_BLOCK}

vec4 sampleTileset(float tileset, vec2 uv) {
  int i = int(tileset - 0.6);
  if (i == 0) return texture2D(u_tilesets[0], uv);
  else if (i == 1) return texture2D(u_tilesets[1], uv);
  else if (i == 2) return texture2D(u_tilesets[2], uv);
  else if (i == 3) return texture2D(u_tilesets[3], uv);
  else if (i == 4) return texture2D(u_tilesets[4], uv);
  else if (i == 5) return texture2D(u_tilesets[5], uv);
  else if (i == 6) return texture2D(u_tilesets[6], uv);
  else if (i == 7) return texture2D(u_tilesets[7], uv);
  else if (i == 8) return texture2D(u_tilesets[8], uv);
  else if (i == 9) return texture2D(u_tilesets[9], uv);
  else if (i == 10) return texture2D(u_tilesets[10], uv);
  else if (i == 11) return texture2D(u_tilesets[11], uv);
  else if (i == 12) return texture2D(u_tilesets[12], uv);
  else if (i == 13) return texture2D(u_tilesets[13], uv);
  return vec4(0.0);
}

void main() {
  vec3 colorRgb = vec3(0.0);
  float coverage = 0.0;

  if (v_tilesets[0] > 0.5) {
    vec4 t0 = sampleTileset(v_tilesets[0], v_uv[0]);
    colorRgb = t0.rgb;
    coverage = 1.0;
  }

  if (v_tilesets[1] > 0.5) {
    vec4 t1 = sampleTileset(v_tilesets[1], v_uv[1]);
    if (coverage < 0.001) {
      colorRgb = t1.rgb;
      coverage = t1.a;
    } else {
      colorRgb = mix(colorRgb, t1.rgb, t1.a);
      coverage = mix(coverage, 1.0, t1.a);
    }
  }

  if (v_tilesets[2] > 0.5) {
    vec4 t2 = sampleTileset(v_tilesets[2], v_uv[2]);
    if (coverage < 0.001) {
      colorRgb = t2.rgb;
      coverage = t2.a;
    } else {
      colorRgb = mix(colorRgb, t2.rgb, t2.a);
      coverage = mix(coverage, 1.0, t2.a);
    }
  }

  if (v_tilesets[3] > 0.5) {
    vec4 t3 = sampleTileset(v_tilesets[3], v_uv[3]);
    if (coverage < 0.001) {
      colorRgb = t3.rgb;
      coverage = t3.a;
    } else {
      colorRgb = mix(colorRgb, t3.rgb, t3.a);
      coverage = mix(coverage, 1.0, t3.a);
    }
  }

  if (coverage < 0.004) {
    discard;
  }

  vec3 lighting = computeW3Lighting(v_normal);
  float shadow = texture2D(u_shadowMap, clamp(v_mapUv, 0.0, 1.0)).a;
  vec3 lit = colorRgb * lighting * mix(vec3(1.0), vec3(0.60, 0.63, 0.70), shadow);

  gl_FragColor = vec4(applyW3Fog(lit), coverage);
}
`;

  const WATER_VS = `
uniform mat4 u_VP;
uniform sampler2D u_heightMap;
uniform sampler2D u_waterHeightMap;
uniform vec2 u_size;
uniform vec2 u_offset;
uniform float u_offsetHeight;
uniform vec2 u_waterUvOffset;
uniform vec2 u_waterScale;

attribute vec2 a_position;
attribute float a_InstanceID;
attribute float a_isWater;

varying vec2 v_uv;
varying vec2 v_mapUv;
varying float v_depth;
varying float v_fogDepth;

void main() {
  if (a_isWater > 0.5) {
    vec2 corner = vec2(mod(a_InstanceID, u_size.x), floor(a_InstanceID / u_size.x));
    vec2 base = corner + a_position;
    vec2 heightUv = (base + vec2(0.5)) / (u_size + vec2(1.0));
    v_mapUv = base / u_size;

    vec2 scale = length(u_waterScale) > 0.001 ? u_waterScale : vec2(0.5);
    v_uv = base * scale + u_waterUvOffset;

    float height = texture2D(u_heightMap, heightUv).a;
    float waterHeight = texture2D(u_waterHeightMap, heightUv).a + u_offsetHeight;
    v_depth = waterHeight - height;

    gl_Position = u_VP * vec4(base * 128.0 + u_offset, waterHeight * 128.0, 1.0);
    v_fogDepth = gl_Position.w;
  } else {
    v_uv = vec2(0.0);
    v_mapUv = vec2(0.0);
    v_depth = 0.0;
    v_fogDepth = 0.0;
    gl_Position = vec4(0.0);
  }
}
`;

  const WATER_FS = `${GL_PREC}
uniform sampler2D u_waterTexture;
uniform sampler2D u_shadowMap;
uniform vec4 u_minDeepColor;
uniform vec4 u_maxDeepColor;
uniform vec4 u_minShallowColor;
uniform vec4 u_maxShallowColor;
uniform float u_minDepth;
uniform float u_deepLevel;
uniform float u_maxDepth;
uniform vec4 u_waterTint;
uniform vec3 u_waterLightTint;

varying vec2 v_uv;
varying vec2 v_mapUv;
varying float v_depth;
${FOG_FS_BLOCK}

void main() {
  if (v_depth <= 0.005) {
    discard;
  }

  float minD = u_minDepth > 0.001 ? u_minDepth : (10.0 / 128.0);
  float deepL = u_deepLevel > 0.001 ? u_deepLevel : (64.0 / 128.0);
  float maxD = u_maxDepth > 0.001 ? u_maxDepth : (72.0 / 128.0);
  if (deepL <= minD + 0.01) deepL = minD + 0.25;
  if (maxD <= deepL + 0.01) maxD = deepL + 0.10;

  float value = clamp(v_depth, 0.0, 2.0);
  vec4 wColor;
  if (value <= deepL) {
    float t = smoothstep(minD, deepL, value);
    wColor = mix(u_minShallowColor, u_maxShallowColor, t) / 255.0;
  } else {
    float t = smoothstep(deepL, maxD, value);
    wColor = mix(u_minDeepColor, u_maxDeepColor, t) / 255.0;
  }
  wColor *= u_waterTint;

  vec4 tex = texture2D(u_waterTexture, v_uv);
  float shoreFade = smoothstep(0.005, 0.18, v_depth);
  float rippleFoam = (1.0 - smoothstep(0.02, 0.14, v_depth)) * smoothstep(0.005, 0.035, v_depth);

  vec3 tint = mix(vec3(1.0), tex.rgb * 1.15, 0.58) * wColor.rgb;
  vec3 finalRgb = mix(tint, max(tint, tex.rgb * 0.90), rippleFoam * 0.30);
  float shadow = texture2D(u_shadowMap, clamp(v_mapUv, 0.0, 1.0)).a;
  float alpha = clamp(wColor.a * mix(0.65, 1.0, tex.a) * shoreFade, 0.0, 0.92);

  if (alpha < 0.01) {
    discard;
  }

  vec3 shaded = finalRgb * u_waterLightTint * mix(vec3(1.0), vec3(0.66, 0.70, 0.76), shadow * 0.75);
  gl_FragColor = vec4(applyW3Fog(shaded), alpha);
}
`;

  const CLIFF_VS = `
uniform mat4 u_VP;
uniform sampler2D u_heightMap;
uniform vec2 u_pixel;
uniform vec2 u_centerOffset;
uniform vec2 u_mapSize;

attribute vec3 a_position;
attribute vec3 a_normal;
attribute vec2 a_uv;
attribute vec3 a_instancePosition;
attribute float a_instanceTexture;

varying vec3 v_normal;
varying vec2 v_uv;
varying float v_texture;
varying vec3 v_position;
varying vec2 v_mapUv;
varying float v_fogDepth;

void main() {
  vec2 halfPixel = u_pixel * 0.5;
  vec2 worldXY = a_instancePosition.xy + a_position.xy;
  vec2 gridPos = (worldXY - u_centerOffset.xy) / 128.0;
  vec2 corner = clamp(floor(gridPos - vec2(0.0005)), vec2(0.0), u_mapSize - vec2(1.0));
  vec2 frac = clamp(gridPos - corner, vec2(0.0), vec2(1.0));

  float bottomLeft = texture2D(u_heightMap, corner * u_pixel + halfPixel).a;
  float bottomRight = texture2D(u_heightMap, (corner + vec2(1.0, 0.0)) * u_pixel + halfPixel).a;
  float topLeft = texture2D(u_heightMap, (corner + vec2(0.0, 1.0)) * u_pixel + halfPixel).a;
  float topRight = texture2D(u_heightMap, (corner + vec2(1.0, 1.0)) * u_pixel + halfPixel).a;

  float bottom = mix(bottomLeft, bottomRight, frac.x);
  float top = mix(topLeft, topRight, frac.x);
  float height = mix(bottom, top, frac.y);

  v_normal = a_normal;
  v_uv = a_uv;
  v_texture = a_instanceTexture;
  v_position = vec3(worldXY, a_position.z + a_instancePosition.z + height * 128.0);
  v_mapUv = clamp((v_position.xy - u_centerOffset) / (u_mapSize * 128.0), 0.0, 1.0);

  gl_Position = u_VP * vec4(v_position, 1.0);
  v_fogDepth = gl_Position.w;
}
`;

  const CLIFF_FS = `${GL_PREC}
uniform sampler2D u_cliffTextures[8];
uniform sampler2D u_shadowMap;

varying vec3 v_normal;
varying vec2 v_uv;
varying float v_texture;
varying vec3 v_position;
varying vec2 v_mapUv;
${FOG_FS_BLOCK}
${DNC_LIGHT_FS_BLOCK}

vec4 sampleCliff(float texIdx, vec2 uv) {
  int i = int(texIdx + 0.1);
  if (i <= 0) return texture2D(u_cliffTextures[0], uv);
  else if (i == 1) return texture2D(u_cliffTextures[1], uv);
  else if (i == 2) return texture2D(u_cliffTextures[2], uv);
  else if (i == 3) return texture2D(u_cliffTextures[3], uv);
  else if (i == 4) return texture2D(u_cliffTextures[4], uv);
  else if (i == 5) return texture2D(u_cliffTextures[5], uv);
  else if (i == 6) return texture2D(u_cliffTextures[6], uv);
  else return texture2D(u_cliffTextures[7], uv);
}

void main() {
  vec4 color = sampleCliff(v_texture, v_uv);

  vec3 lighting = computeW3Lighting(v_normal);
  float shadow = texture2D(u_shadowMap, clamp(v_mapUv, 0.0, 1.0)).a;
  vec3 lit = color.rgb * lighting * mix(vec3(1.0), vec3(0.60, 0.63, 0.70), shadow);

  gl_FragColor = vec4(applyW3Fog(lit), color.a);
}
`;

  const SPLAT_VS = `
uniform mat4 u_VP;
uniform sampler2D u_heightMap;
uniform vec2 u_offset;
uniform vec2 u_size;

attribute vec2 a_position;
attribute vec2 a_uv;
attribute vec4 a_color;

varying vec2 v_uv;
varying vec4 v_color;
varying vec2 v_mapUv;
varying float v_fogDepth;

void main() {
  vec2 pixel = 1.0 / (u_size + vec2(1.0));
  vec2 halfPixel = pixel * 0.5;
  vec2 gridPos = (a_position - u_offset) / 128.0;
  vec2 corner = clamp(floor(gridPos), vec2(0.0), u_size - vec2(1.0));
  vec2 frac = clamp(gridPos - corner, vec2(0.0), vec2(1.0));

  float h00 = texture2D(u_heightMap, corner * pixel + halfPixel).a;
  float h10 = texture2D(u_heightMap, (corner + vec2(1.0, 0.0)) * pixel + halfPixel).a;
  float h01 = texture2D(u_heightMap, (corner + vec2(0.0, 1.0)) * pixel + halfPixel).a;
  float h11 = texture2D(u_heightMap, (corner + vec2(1.0, 1.0)) * pixel + halfPixel).a;
  float height = mix(mix(h00, h10, frac.x), mix(h01, h11, frac.x), frac.y) * 128.0;

  v_uv = a_uv;
  v_color = a_color;
  v_mapUv = clamp((a_position - u_offset) / (u_size * 128.0), 0.0, 1.0);

  gl_Position = u_VP * vec4(a_position, height + 1.8, 1.0);
  v_fogDepth = gl_Position.w;
}
`;

  const SPLAT_FS = `${GL_PREC}
uniform sampler2D u_splatTexture;
uniform sampler2D u_shadowMap;
uniform vec3 u_splatLightTint;
uniform float u_additive;

varying vec2 v_uv;
varying vec4 v_color;
varying vec2 v_mapUv;
${FOG_FS_BLOCK}

void main() {
  vec4 tex = texture2D(u_splatTexture, v_uv) * v_color;
  if (tex.a < 0.01) discard;
  if (u_additive > 0.5) {
    gl_FragColor = tex;
  } else {
    float shadow = texture2D(u_shadowMap, clamp(v_mapUv, 0.0, 1.0)).a;
    vec3 lit = tex.rgb * u_splatLightTint * mix(vec3(1.0), vec3(0.60, 0.63, 0.70), shadow);
    gl_FragColor = vec4(applyW3Fog(lit), tex.a);
  }
}
`;

  function upgradeViewerShaders(viewer) {
    if (!viewer || viewer._w3sShadersUpgraded) return;
    viewer._w3sShadersUpgraded = true;
    const webgl = viewer.webgl;
    try {
      const g = webgl.createShader(GROUND_VS, GROUND_FS);
      if (g) viewer.groundShader = g;
      const w = webgl.createShader(WATER_VS, WATER_FS);
      if (w) viewer.waterShader = w;
      const c = webgl.createShader(CLIFF_VS, CLIFF_FS);
      if (c) viewer.cliffShader = c;
      const s = webgl.createShader(SPLAT_VS, SPLAT_FS);
      if (s) viewer.splatShader = s;
    } catch (err) {
      console.error("[W3Studio Fixes] Shader compile error:", err);
    }
  }

  // =========================================================================
  // 2. Safe SLK row fallbacks & texture helpers
  // =========================================================================
  function makeClonedRow(templateRow, overrides) {
    const newMap = Object.assign({}, templateRow && templateRow.map ? templateRow.map : {}, overrides || {});
    return {
      map: newMap,
      string(key) {
        const v = newMap[key] !== undefined ? newMap[key] : newMap[String(key).toLowerCase()];
        return v !== undefined && v !== null ? String(v) : "";
      },
      number(key) {
        const v = newMap[key] !== undefined ? newMap[key] : newMap[String(key).toLowerCase()];
        const n = Number(v);
        return Number.isFinite(n) ? n : 0;
      },
    };
  }

  function installSafeBaseDataWrappers(viewer) {
    if (!viewer || viewer._w3sDataWrapped) return;
    viewer._w3sDataWrapped = true;

    const wrapTable = (table, fallbackFn) => {
      if (!table || typeof table.getRow !== "function" || table._w3sWrapped) return;
      table._w3sWrapped = true;
      const origGetRow = table.getRow.bind(table);
      table.getRow = function (id) {
        let row = origGetRow(id);
        if (!row && id !== undefined && id !== null) {
          row = origGetRow(String(id).trim());
        }
        if (!row && table.map instanceof Map) {
          const wanted = String(id || "").trim().toLowerCase();
          for (const [k, v] of table.map.entries()) {
            if (String(k).toLowerCase() === wanted) {
              row = v;
              break;
            }
          }
        }
        if (!row) {
          row = fallbackFn(table, id);
        }
        return row;
      };
    };

    wrapTable(viewer.terrainData, (table, id) => {
      const first = table.map instanceof Map ? table.map.values().next().value : null;
      if (!first) return makeClonedRow(null, { tileID: String(id || "Ldrt"), dir: "TerrainArt\\LordaeronSummer", file: "Lords_Dirt" });
      console.warn("[W3Studio Fixes] Unknown terrain tileset ID:", id, "-> falling back to", first.string("tileID"));
      return makeClonedRow(first, { tileID: String(id || first.string("tileID")) });
    });

    wrapTable(viewer.cliffTypesData, (table, id) => {
      const preferred = table.map instanceof Map ? (table.map.get("CLdi") || table.map.values().next().value) : null;
      if (!preferred) return makeClonedRow(null, { cliffID: String(id || "CLdi"), cliffModelDir: "Cliffs", rampModelDir: "CliffTrans", texDir: "ReplaceableTextures\\Cliff", texFile: "Cliff0", groundTile: "Ldrt" });
      console.warn("[W3Studio Fixes] Unknown cliff tileset ID:", id, "-> falling back to", preferred.string("cliffID"));
      return makeClonedRow(preferred, null);
    });

    wrapTable(viewer.waterData, (table, id) => {
      const preferred = table.map instanceof Map ? (table.map.get("LSha") || table.map.values().next().value) : null;
      console.warn("[W3Studio Fixes] Unknown water ID:", id, "-> falling back to LSha");
      return preferred || makeClonedRow(null, { height: 0, texRate: 20, numTex: 45, texFile: "ReplaceableTextures\\Water\\Water", minDepth: 10, deepLevel: 64, maxDepth: 72, Smin_R: 255, Smin_G: 255, Smin_B: 255, Smin_A: 0, Smax_R: 60, Smax_G: 160, Smax_B: 160, Smax_A: 200, Dmin_R: 255, Dmin_G: 255, Dmin_B: 255, Dmin_A: 128, Dmax_R: 15, Dmax_G: 40, Dmax_B: 80, Dmax_A: 245 });
    });
  }

  function createFallbackTexture(gl) {
    const tex = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, tex);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array([96, 96, 96, 255]));
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    return tex;
  }

  function ensureFallbackResource(map) {
    if (!map._w3sFallbackTex) {
      map._w3sFallbackTex = {
        width: 256,
        height: 256,
        webglResource: createFallbackTexture(map.viewer.gl),
      };
    }
    return map._w3sFallbackTex;
  }

  function normalizeAssetPath(path) {
    if (typeof path !== "string") return "";
    let p = path.trim();
    if (!p) return "";
    // Collapse accidental double extensions (.mdl.mdx, .mdx.mdx, .tga.blp, .blp.blp)
    p = p.replace(/\.mdl\.mdx$/i, ".mdx");
    p = p.replace(/\.mdx\.mdx$/i, ".mdx");
    p = p.replace(/\.tga\.blp$/i, ".tga");
    p = p.replace(/\.blp\.blp$/i, ".blp");
    p = p.replace(/\.dds\.blp$/i, ".dds");
    const lower = p.toLowerCase();
    const base = lower.split(/[\\/]/).pop() || "";
    if (!base || base === ".mdx" || base === ".blp" || base === "none" || base === "none.mdx" || base === "none.blp" || base === "_.mdx" || base === "_.blp" || base === "0.mdx" || base === "0.blp" || base === "_.tga" || base === "none.tga" || base === "0.tga") {
      return "";
    }
    return p;
  }

  // =========================================================================
  // 3. Patch War3MapViewer prototype (extra SLK/TXT + shaders)
  // =========================================================================
  function ensureExtraBaseFiles(viewer) {
    if (viewer._w3sExtraBasePromise) return viewer._w3sExtraBasePromise;
    viewer._w3sExtraBasePromise = (async () => {
      try {
        const fetchText = async (rel) => {
          const resp = await fetch("https://w3studio.local/mpq/" + rel.replace(/\\/g, "/"));
          return resp.ok ? await resp.text() : null;
        };
        const [splatText, wedText] = await Promise.all([
          fetchText("Splats/UberSplatData.slk"),
          fetchText("UI/WorldEditData.txt")
        ]);
        if (splatText && viewer.waterData && viewer.waterData.constructor) {
          const tbl = new viewer.waterData.constructor();
          tbl.load(splatText);
          viewer._w3sUberSplatData = tbl;
        }
        if (wedText) {
          viewer._w3sTerrainLights = parseIniSection(wedText, "TerrainLights");
        }
      } catch (err) {
        console.debug("[W3Studio Fixes] Extra base files skipped:", err);
      }
    })();
    return viewer._w3sExtraBasePromise;
  }

  const origLoadBaseFiles = War3MapViewer.prototype.loadBaseFiles;
  War3MapViewer.prototype.loadBaseFiles = async function () {
    ensureExtraBaseFiles(this);
    const res = await origLoadBaseFiles.apply(this, arguments);
    await ensureExtraBaseFiles(this);
    upgradeViewerShaders(this);
    installSafeBaseDataWrappers(this);
    return res;
  };

  const origViewerLoadMap = War3MapViewer.prototype.loadMap;
  War3MapViewer.prototype.loadMap = function () {
    window.__w3sViewer = this;
    ensureExtraBaseFiles(this);
    upgradeViewerShaders(this);
    installSafeBaseDataWrappers(this);
    return origViewerLoadMap.apply(this, arguments);
  };

  // =========================================================================
  // 4. Patch War3Map prototype methods
  // =========================================================================
  const proto = War3Map.prototype;

  // 4a. Path sanitization in load() & W3I metadata / DNC light setup
  const origMapLoad = proto.load;
  proto.load = function (path) {
    const cleaned = normalizeAssetPath(path);
    if (!cleaned) {
      return Promise.resolve(null);
    }
    return origMapLoad.call(this, cleaned);
  };

  async function loadMapDncLight(map) {
    try {
      const viewer = map.viewer;
      await ensureExtraBaseFiles(viewer);
      const env = (map._w3sVisuals && map._w3sVisuals.lightEnv) || (map.solverParams && map.solverParams.tileset || "L").toUpperCase();
      const mapEntry = viewer._w3sTerrainLights && viewer._w3sTerrainLights.get(env);
      if (!mapEntry) return;
      const mdxPath = mapEntry.replace(/\.mdl$/i, ".mdx").replace(/\\/g, "/");
      const resp = await fetch("https://w3studio.local/mpq/" + mdxPath);
      if (!resp.ok || map._w3sDead) return;
      const buf = await resp.arrayBuffer();
      if (!buf || map._w3sDead) return;
      const dncModel = readLightMdx(new Uint8Array(buf));
      if (!dncModel) return;
      map._w3sDncModel = dncModel;
      const hour = Number.isFinite(viewer._w3sLightingHour) ? viewer._w3sLightingHour : 12;
      map._w3sSceneLight = evaluateDncLight(dncModel, hour);
    } catch (err) {
      console.debug("[W3Studio Fixes] DNC lighting load skipped:", err);
    }
  }

  const origLoadMapInfo = proto.loadMapInformation;
  proto.loadMapInformation = function () {
    origLoadMapInfo.apply(this, arguments);
    const wpmFile = this.map && this.map.get("war3map.wpm");
    this._w3sPathing = wpmFile ? parseWpmPathing(wpmFile.bytes()) : null;
    const w3iFile = this.map && this.map.get("war3map.w3i");
    this._w3sVisuals = (w3iFile && parseW3iVisuals(w3iFile.bytes())) || {
      flags: MAP_FLAG_CLIFF_SHORES | MAP_FLAG_ROLLING_SHORES,
      tileset: ((this.solverParams && this.solverParams.tileset) || "L").toUpperCase(),
      lightEnv: ((this.solverParams && this.solverParams.tileset) || "L").toUpperCase(),
      fog: null,
      waterTint: [1, 1, 1, 1],
      globalWeather: "",
      cameraBounds: null,
      complements: [0, 0, 0, 0],
      playableSize: [0, 0],
      cliffShores: true,
      rollingShores: true
    };
    this._w3sDncPromise = loadMapDncLight(this);
  };

  // 4b. loadTerrainCliffsAndWater(): pre-detect static shadow map, guard null textures, extract Water.slk params
  const origLoadTerrain = proto.loadTerrainCliffsAndWater;
  proto.loadTerrainCliffsAndWater = async function () {
    installSafeBaseDataWrappers(this.viewer);
    upgradeViewerShaders(this.viewer);

    // Synchronously detect war3map.shd BEFORE loadDoodadsAndDestructibles / loadUnitsAndItems run
    const shdFile = this.map && this.map.get("war3map.shd");
    let hasShdFile = false;
    if (shdFile) {
      try {
        const bytes = shdFile.bytes();
        hasShdFile = !!(bytes && bytes.length >= 256);
      } catch (e) {
        hasShdFile = false;
      }
    }
    this._mapHasShdFile = hasShdFile;
    this.hasStaticShadowMap = hasShdFile;
    this._waterUvOffset = [0, 0];
    this._waterMinDepth = 10 / 128;
    this._waterDeepLevel = 64 / 128;
    this._waterMaxDepth = 72 / 128;
    this._waterScrollRate = [0.02, 0.01];

    // Look up Water.slk depth & flow rate for this map's tileset
    try {
      const w3eFile = this.map && this.map.get("war3map.w3e");
      if (w3eFile) {
        const wBytes = w3eFile.bytes();
        // 'W3E!' header: 4B magic + 4B version + 1B tileset char at offset 8
        const tsChar = wBytes && wBytes.length > 8 ? String.fromCharCode(wBytes[8]) : "L";
        const wRow = this.viewer.waterData.getRow(tsChar + "Sha");
        if (wRow) {
          const minD = wRow.number("minDepth");
          const deepL = wRow.number("deepLevel");
          const maxD = wRow.number("maxDepth");
          if (minD > 0) this._waterMinDepth = minD / 128;
          if (deepL > 0) this._waterDeepLevel = deepL / 128;
          if (maxD > 0) this._waterMaxDepth = maxD / 128;
          const rx = wRow.number("rateX");
          const ry = wRow.number("rateY");
          if (Number.isFinite(rx) || Number.isFinite(ry)) {
            this._waterScrollRate = [rx * 0.0025, ry * 0.0025];
          }
        }
      }
    } catch (err) {
      console.debug("[W3Studio Fixes] Water.slk meta read skipped:", err);
    }

    await origLoadTerrain.apply(this, arguments);

    // Replace any null textures with 1x1 safe fallback so renderGround / renderCliffs never throw
    const fallbackTex = ensureFallbackResource(this);
    if (Array.isArray(this.tilesetTextures)) {
      for (let i = 0; i < this.tilesetTextures.length; i++) {
        if (!this.tilesetTextures[i]) {
          this.tilesetTextures[i] = fallbackTex;
        }
      }
    }
    if (Array.isArray(this.cliffTextures)) {
      if (this.cliffTextures.length === 0) {
        this.cliffTextures.push(fallbackTex);
      } else {
        for (let i = 0; i < this.cliffTextures.length; i++) {
          if (!this.cliffTextures[i]) {
            this.cliffTextures[i] = this.cliffTextures[0] || fallbackTex;
          }
        }
      }
    }
    if (Array.isArray(this.waterTextures)) {
      this.waterTextures = this.waterTextures.filter(Boolean);
    }

    // Darken WC3 boundary (0x80) and mapEdge cells directly on terrain shadowMap
    applyBoundaryToShadowMap(this);

    // Spawn classic WC3 shoreline foam emitters after terrain & water corners are ready
    try {
      const w3eFile = this.map && this.map.get("war3map.w3e");
      const tsChar = w3eFile && w3eFile.bytes().length > 8
        ? String.fromCharCode(w3eFile.bytes()[8])
        : ((this.solverParams && this.solverParams.tileset) || "L").toUpperCase();
      spawnShorelineInstances(this, this.viewer.waterData.getRow(tsChar + "Sha"));
    } catch (err) {
      console.debug("[W3Studio Fixes] Shoreline setup skipped:", err);
    }
  };

  async function spawnShorelineInstances(map, waterRow) {
    if (!waterRow || !map.corners || map.corners.length < 2 || map._w3sDead) return;
    const vis = map._w3sVisuals || { cliffShores: true, rollingShores: true };
    if (!vis.cliffShores && !vis.rollingShores) return;

    const shoreDir = (waterRow.string("shoreDir") || "").replace(/[\\/]+$/, "");
    const sFile = waterRow.string("shoreSFile");
    const ocFile = waterRow.string("shoreOCFile");
    const icFile = waterRow.string("shoreICFile");
    if (!shoreDir || !sFile || !ocFile || !icFile) return;

    const CORNERS = [[-1, -1], [1, -1], [1, 1], [-1, 1]];
    const placements = { straight: [], outside: [], inside: [] };
    const cols = map.columns, rows = map.rows;
    const layers = map.effectiveLayers;
    const offX = map.centerOffset[0], offY = map.centerOffset[1];
    const wOff = map.waterHeightOffset || 0;

    for (let y = 0; y < rows; y++) {
      const row0 = map.corners[y], row1 = map.corners[y + 1];
      if (!row0 || !row1) continue;
      for (let x = 0; x < cols; x++) {
        const cs = [row0[x], row0[x + 1], row1[x + 1], row1[x]];
        if (!cs[0].water && !cs[1].water && !cs[2].water && !cs[3].water) continue;
        let underCount = 0;
        const under = [false, false, false, false];
        let maxWaterZ = -1e9;
        for (let k = 0; k < 4; k++) {
          const c = cs[k];
          if (!c.water) continue;
          const lx = (k === 1 || k === 2) ? x + 1 : x;
          const ly = (k >= 2) ? y + 1 : y;
          const effL = (layers && layers[ly] && layers[ly][lx] !== undefined) ? layers[ly][lx] : c.layerHeight;
          const groundZ = (c.groundHeight + effL - 2) * 128;
          const waterZ = (c.waterHeight + wOff) * 128;
          if (waterZ > maxWaterZ) maxWaterZ = waterZ;
          if (waterZ - groundZ > SHORE_DEPTH) {
            under[k] = true;
            underCount++;
          }
        }
        // Only tiles with +X +Y corner (index 2) deep enough under water get shoreline waves
        if (underCount === 0 || underCount === 4 || !under[2]) continue;
        const isCliff = !(cs[0].layerHeight === cs[1].layerHeight && cs[0].layerHeight === cs[2].layerHeight && cs[0].layerHeight === cs[3].layerHeight);
        if (isCliff ? !vis.cliffShores : !vis.rollingShores) continue;

        const cx = offX + (x + 0.5) * 128;
        const cy = offY + (y + 0.5) * 128;
        const level = maxWaterZ + 0.5;
        if (underCount === 2) {
          let dx = 0, dy = 0;
          for (let k = 0; k < 4; k++) if (!under[k]) { dx += CORNERS[k][0]; dy += CORNERS[k][1]; }
          if (dx === 0 && dy === 0) continue;
          placements.straight.push([cx, cy, level, Math.atan2(dy, dx) - Math.PI]);
        } else if (underCount === 3) {
          let lx = 0, ly = 0;
          for (let k = 0; k < 4; k++) if (!under[k]) { lx = CORNERS[k][0]; ly = CORNERS[k][1]; break; }
          placements.outside.push([cx, cy, level, Math.atan2(ly, lx) - Math.atan2(1, -1)]);
        } else {
          let wx = 0, wy = 0;
          for (let k = 0; k < 4; k++) if (under[k]) { wx = CORNERS[k][0]; wy = CORNERS[k][1]; break; }
          placements.inside.push([cx, cy, level, Math.atan2(wy, wx) - Math.atan2(1, -1)]);
        }
      }
    }

    const total = placements.straight.length + placements.outside.length + placements.inside.length;
    if (total === 0) return;

    const specs = [
      { key: "straight", path: `${shoreDir}\\${sFile}\\${sFile}0.mdx` },
      { key: "outside", path: `${shoreDir}\\${ocFile}\\${ocFile}0.mdx` },
      { key: "inside", path: `${shoreDir}\\${icFile}\\${icFile}0.mdx` }
    ];
    map._w3sShoreInstances = map._w3sShoreInstances || [];
    const maxCap = 750;
    const stride = total > maxCap ? Math.ceil(total / maxCap) : 1;

    await Promise.all(specs.map(async ({ key, path }) => {
      const list = placements[key];
      if (!list.length) return;
      const model = await map.load(path);
      if (!model || map._w3sDead) return;
      for (let i = 0; i < list.length; i += stride) {
        const [px, py, pz, angle] = list[i];
        const inst = model.addInstance();
        inst.move([px, py, pz]);
        inst.rotateLocal([0, 0, Math.sin(angle * 0.5), Math.cos(angle * 0.5)]);
        if (typeof inst.setSequenceLoopMode === "function") inst.setSequenceLoopMode(2);
        if (model.sequences && model.sequences.length > 0 && typeof inst.setSequence === "function") inst.setSequence(0);
        inst.setScene(map.worldScene);
        map._w3sShoreInstances.push(inst);
      }
    }));
  }

  // 4c. Border cliffs + safe index clamping
  proto.isCliff = function (x, y) {
    if (x < 0 || x >= this.columns || y < 0 || y >= this.rows) return false;
    const c = this.corners;
    const c00 = c[y][x];
    const c10 = c[y][x + 1];
    const c01 = c[y + 1][x];
    const c11 = c[y + 1][x + 1];
    if (c00.ramp && c10.ramp && c01.ramp && c11.ramp) return false;
    if (this.effectiveLayers && this.effectiveLayers.length > 0) {
      const e00 = this.effectiveLayers[y][x];
      const e10 = this.effectiveLayers[y][x + 1];
      const e01 = this.effectiveLayers[y + 1][x];
      const e11 = this.effectiveLayers[y + 1][x + 1];
      return e00 % 1 === 0 && e10 % 1 === 0 && e01 % 1 === 0 && e11 % 1 === 0 && (e00 !== e10 || e00 !== e01 || e00 !== e11);
    }
    const h00 = c00.layerHeight;
    return h00 !== c10.layerHeight || h00 !== c01.layerHeight || h00 !== c11.layerHeight;
  };

  proto.getVariation = function (tileIndex, variation) {
    const tex = this.tilesetTextures && this.tilesetTextures[tileIndex];
    if (!tex) return 0;
    return tex.width > tex.height ? (variation < 16 ? 16 + variation : variation === 16 ? 15 : 0) : (variation === 0 ? 0 : 15);
  };

  proto.cliffGroundIndex = function (cliffIndex) {
    if (!this.cliffTilesets || this.cliffTilesets.length === 0) return 0;
    const idx = cliffIndex >= 0 && cliffIndex < this.cliffTilesets.length ? cliffIndex : 0;
    const cliffRow = this.cliffTilesets[idx] || this.cliffTilesets[0];
    if (!cliffRow) return 0;
    const groundTile = cliffRow.string("groundTile");
    const tilesets = this.tilesets || [];
    for (let i = 0; i < tilesets.length; i++) {
      if (tilesets[i] && tilesets[i].string("tileID") === groundTile) return i;
    }
    return 0;
  };

  proto.cornerTexture = function (x, y) {
    const c = this.corners;
    const cols = this.columns;
    const rows = this.rows;
    const corner = c[y][x];
    if (!corner.ramp) {
      for (let dy = -1; dy < 1; dy++) {
        for (let dx = -1; dx < 1; dx++) {
          const tx = x + dx;
          const ty = y + dy;
          if (tx >= 0 && tx < cols && ty >= 0 && ty < rows) {
            const isC = this.cliffTileMask && this.cliffTileMask.length > 0 ? this.cliffTileMask[ty * cols + tx] === 1 : this.isCliff(tx, ty);
            if (isC) {
              let ct = c[ty][tx].cliffTexture;
              if (ct === 15 || !this.cliffTilesets || ct >= this.cliffTilesets.length) {
                ct = Math.min(1, Math.max(0, (this.cliffTilesets ? this.cliffTilesets.length : 1) - 1));
              }
              return this.cliffGroundIndex(ct);
            }
          }
        }
      }
    }
    return corner.blight ? this.blightTextureIndex : Math.min(corner.groundTexture, Math.max(0, (this.tilesets ? this.tilesets.length : 1) - 1));
  };

  // 4d. rebuildCliffModels(): fallback to variation 0 on missing cliff variation + clamp texture indices
  const origRebuildCliffs = proto.rebuildCliffModels;
  proto.rebuildCliffModels = async function (cliffModelsMap) {
    if (cliffModelsMap && typeof cliffModelsMap === "object") {
      const maxCliffTex = Math.max(0, (this.cliffTilesets ? this.cliffTilesets.length : 1) - 1);
      for (const entry of Object.values(cliffModelsMap)) {
        if (entry && Array.isArray(entry.textures)) {
          for (let i = 0; i < entry.textures.length; i++) {
            if (entry.textures[i] > maxCliffTex || entry.textures[i] < 0) {
              entry.textures[i] = maxCliffTex > 0 ? Math.min(entry.textures[i] === 15 ? 1 : 0, maxCliffTex) : 0;
            }
          }
        }
      }
    }
    const viewer = this.viewer;
    const origLoadBaseFile = viewer.loadBaseFile.bind(viewer);
    // Intercept missing cliff variation inside rebuildCliffModels -> try 0.mdx before giving up
    viewer.loadBaseFile = async (path, type) => {
      let res = await origLoadBaseFile(path, type);
      if (!res && typeof path === "string" && /^Doodads\\Terrain\\/i.test(path) && /[1-9]\.mdx$/i.test(path)) {
        res = await origLoadBaseFile(path.replace(/[1-9]\.mdx$/i, "0.mdx"), type);
      }
      return res;
    };
    try {
      return await origRebuildCliffs.apply(this, arguments);
    } finally {
      viewer.loadBaseFile = origLoadBaseFile;
    }
  };

  // 4e. Render methods with clean WebGL state resets (no CULL_FACE leaks!)
  proto.renderGround = function () {
    if (!this.terrainReady || !this.heightMap) return;
    const gl = this.viewer.gl;
    const webgl = this.viewer.webgl;
    const ext = webgl.extensions.ANGLE_instanced_arrays;
    const shader = this.viewer.groundShader;
    const u = shader.uniforms;
    const a = shader.attribs;
    const textures = this.tilesetTextures || [];
    const count = textures.length;
    if (count === 0) return;

    gl.disable(gl.CULL_FACE);
    gl.enable(gl.DEPTH_TEST);
    gl.depthMask(true);
    gl.enable(gl.BLEND);
    gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);

    webgl.useShader(shader);
    gl.uniformMatrix4fv(u.u_VP, false, this.worldScene.camera.viewProjectionMatrix);
    gl.uniform2fv(u.u_offset, this.centerOffset);
    gl.uniform2f(u.u_size, this.columns, this.rows);
    gl.uniform1i(u.u_shadowMap, 14);
    gl.uniform1i(u.u_heightMap, 15);
    setFogUniforms(gl, u, this._w3sVisuals && this._w3sVisuals.fog);
    setLightingUniforms(gl, u, this._w3sSceneLight);
    gl.activeTexture(gl.TEXTURE14);
    gl.bindTexture(gl.TEXTURE_2D, this.shadowMap);
    gl.activeTexture(gl.TEXTURE15);
    gl.bindTexture(gl.TEXTURE_2D, this.heightMap);

    gl.bindBuffer(gl.ARRAY_BUFFER, this.vertexBuffer);
    gl.vertexAttribPointer(a.a_position, 2, gl.FLOAT, false, 0, 0);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.instanceBuffer);
    gl.vertexAttribPointer(a.a_InstanceID, 1, gl.FLOAT, false, 0, 0);
    ext.vertexAttribDivisorANGLE(a.a_InstanceID, 1);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.textureBuffer);
    gl.vertexAttribPointer(a.a_textures, 4, gl.UNSIGNED_BYTE, false, 0, 0);
    ext.vertexAttribDivisorANGLE(a.a_textures, 1);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.variationBuffer);
    gl.vertexAttribPointer(a.a_variations, 4, gl.UNSIGNED_BYTE, false, 0, 0);
    ext.vertexAttribDivisorANGLE(a.a_variations, 1);
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, this.faceBuffer);

    const fallbackTex = ensureFallbackResource(this);
    gl.uniform1f(u.u_baseTileset, 0);
    for (let i = 0, n = Math.min(count, 14); i < n; i++) {
      const tex = textures[i] || fallbackTex;
      gl.uniform1f(u[`u_extended[${i}]`], tex.width > tex.height ? 1 : 0);
      gl.uniform1i(u[`u_tilesets[${i}]`], i);
      webgl.bindTexture(tex, i);
    }
    ext.drawElementsInstancedANGLE(gl.TRIANGLES, 6, gl.UNSIGNED_BYTE, 0, this.rows * this.columns);

    if (count > 14) {
      gl.uniform1f(u.u_baseTileset, 14);
      for (let i = 0, n = Math.min(count - 14, 14); i < n; i++) {
        const tex = textures[i + 14] || fallbackTex;
        gl.uniform1f(u[`u_extended[${i}]`], tex.width > tex.height ? 1 : 0);
        gl.uniform1i(u[`u_tilesets[${i}]`], i);
        webgl.bindTexture(tex, i);
      }
      ext.drawElementsInstancedANGLE(gl.TRIANGLES, 6, gl.UNSIGNED_BYTE, 0, this.rows * this.columns);
    }

    ext.vertexAttribDivisorANGLE(a.a_textures, 0);
    ext.vertexAttribDivisorANGLE(a.a_variations, 0);
    ext.vertexAttribDivisorANGLE(a.a_InstanceID, 0);
  };

  proto.renderCliffs = function () {
    if (!this.cliffsReady || !this.cliffModels || this.cliffModels.length === 0) return;
    if (!this.cliffTextures || this.cliffTextures.length === 0) return;

    const gl = this.viewer.gl;
    const webgl = this.viewer.webgl;
    const instExt = webgl.extensions.ANGLE_instanced_arrays;
    const vaoExt = webgl.extensions.OES_vertex_array_object;
    const shader = this.viewer.cliffShader;
    const a = shader.attribs;
    const u = shader.uniforms;

    gl.disable(gl.CULL_FACE);
    gl.enable(gl.DEPTH_TEST);
    gl.depthMask(true);
    gl.disable(gl.BLEND);

    shader.use();
    gl.uniformMatrix4fv(u.u_VP, false, this.worldScene.camera.viewProjectionMatrix);
    gl.uniform1i(u.u_heightMap, 0);
    gl.uniform2f(u.u_pixel, 1 / (this.columns + 1), 1 / (this.rows + 1));
    gl.uniform2fv(u.u_centerOffset, this.centerOffset);
    gl.uniform2f(u.u_mapSize, this.columns, this.rows);
    setFogUniforms(gl, u, this._w3sVisuals && this._w3sVisuals.fog);
    setLightingUniforms(gl, u, this._w3sSceneLight);

    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, this.cliffHeightMap);

    const tex0 = this.cliffTextures[0] && this.cliffTextures[0].webglResource
      ? this.cliffTextures[0].webglResource
      : ensureFallbackResource(this).webglResource;

    for (let i = 0; i < 8; i++) {
      const cTex = this.cliffTextures[i] && this.cliffTextures[i].webglResource
        ? this.cliffTextures[i].webglResource
        : tex0;
      const unit = 1 + i;
      if (u[`u_cliffTextures[${i}]`] !== undefined) {
        gl.uniform1i(u[`u_cliffTextures[${i}]`], unit);
      }
      gl.activeTexture(gl.TEXTURE0 + unit);
      gl.bindTexture(gl.TEXTURE_2D, cTex);
    }

    if (u.u_shadowMap !== undefined) {
      gl.uniform1i(u.u_shadowMap, 9);
      gl.activeTexture(gl.TEXTURE9);
      gl.bindTexture(gl.TEXTURE_2D, this.shadowMap);
    }

    if (!vaoExt) {
      instExt.vertexAttribDivisorANGLE(a.a_instancePosition, 1);
      instExt.vertexAttribDivisorANGLE(a.a_instanceTexture, 1);
    }
    for (const cm of this.cliffModels) {
      cm.render(shader);
    }
    if (!vaoExt) {
      instExt.vertexAttribDivisorANGLE(a.a_instancePosition, 0);
      instExt.vertexAttribDivisorANGLE(a.a_instanceTexture, 0);
    }
  };

  proto.renderWater = function () {
    if (!this.terrainReady || !this.waterTextures || this.waterTextures.length === 0) return;
    const waterTex = this.waterTextures[0 | this.waterIndex] || this.waterTextures[0];
    if (!waterTex) return;

    const gl = this.viewer.gl;
    const webgl = this.viewer.webgl;
    const ext = webgl.extensions.ANGLE_instanced_arrays;
    const shader = this.viewer.waterShader;
    const u = shader.uniforms;
    const a = shader.attribs;

    gl.disable(gl.CULL_FACE);
    gl.enable(gl.DEPTH_TEST);
    gl.depthMask(false);
    gl.enable(gl.BLEND);
    gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);

    webgl.useShader(shader);
    gl.uniformMatrix4fv(u.u_VP, false, this.worldScene.camera.viewProjectionMatrix);
    gl.uniform2fv(u.u_offset, this.centerOffset);
    gl.uniform2f(u.u_size, this.columns, this.rows);
    gl.uniform1i(u.u_heightMap, 0);
    gl.uniform1i(u.u_waterHeightMap, 1);
    gl.uniform1i(u.u_waterTexture, 2);
    gl.uniform1i(u.u_shadowMap, 3);
    gl.uniform1f(u.u_offsetHeight, this.waterHeightOffset);
    gl.uniform4fv(u.u_maxDeepColor, this.maxDeepColor);
    gl.uniform4fv(u.u_minDeepColor, this.minDeepColor);
    gl.uniform4fv(u.u_maxShallowColor, this.maxShallowColor);
    gl.uniform4fv(u.u_minShallowColor, this.minShallowColor);

    if (u.u_minDepth !== undefined) gl.uniform1f(u.u_minDepth, this._waterMinDepth || 10 / 128);
    if (u.u_deepLevel !== undefined) gl.uniform1f(u.u_deepLevel, this._waterDeepLevel || 64 / 128);
    if (u.u_maxDepth !== undefined) gl.uniform1f(u.u_maxDepth, this._waterMaxDepth || 72 / 128);
    if (u.u_waterUvOffset !== undefined) {
      const off = this._waterUvOffset || [0, 0];
      gl.uniform2f(u.u_waterUvOffset, off[0], off[1]);
    }
    if (u.u_waterScale !== undefined) gl.uniform2f(u.u_waterScale, 0.5, 0.5);
    if (u.u_waterTint !== undefined) {
      const wt = (this._w3sVisuals && this._w3sVisuals.waterTint) || [1, 1, 1, 1];
      gl.uniform4f(u.u_waterTint, wt[0], wt[1], wt[2], wt[3]);
    }
    if (u.u_waterLightTint !== undefined) {
      const sl = this._w3sSceneLight;
      if (sl) {
        gl.uniform3f(
          u.u_waterLightTint,
          Math.max(0.15, Math.min(1.05, sl.ambient[0] + sl.diffuse[0] * 0.55)),
          Math.max(0.15, Math.min(1.05, sl.ambient[1] + sl.diffuse[1] * 0.55)),
          Math.max(0.15, Math.min(1.05, sl.ambient[2] + sl.diffuse[2] * 0.55))
        );
      } else {
        gl.uniform3f(u.u_waterLightTint, 1, 1, 1);
      }
    }
    setFogUniforms(gl, u, this._w3sVisuals && this._w3sVisuals.fog);

    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, this.heightMap);
    gl.activeTexture(gl.TEXTURE1);
    gl.bindTexture(gl.TEXTURE_2D, this.waterHeightMap);
    gl.activeTexture(gl.TEXTURE3);
    gl.bindTexture(gl.TEXTURE_2D, this.shadowMap);

    webgl.bindTexture(waterTex, 2);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.REPEAT);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.REPEAT);

    gl.bindBuffer(gl.ARRAY_BUFFER, this.vertexBuffer);
    gl.vertexAttribPointer(a.a_position, 2, gl.FLOAT, false, 8, 0);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.instanceBuffer);
    gl.vertexAttribPointer(a.a_InstanceID, 1, gl.FLOAT, false, 4, 0);
    ext.vertexAttribDivisorANGLE(a.a_InstanceID, 1);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.waterBuffer);
    gl.vertexAttribPointer(a.a_isWater, 1, gl.UNSIGNED_BYTE, false, 1, 0);
    ext.vertexAttribDivisorANGLE(a.a_isWater, 1);
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, this.faceBuffer);
    ext.drawElementsInstancedANGLE(gl.TRIANGLES, 6, gl.UNSIGNED_BYTE, 0, this.rows * this.columns);
    ext.vertexAttribDivisorANGLE(a.a_isWater, 0);
    ext.vertexAttribDivisorANGLE(a.a_InstanceID, 0);

    gl.depthMask(true);
  };

  // 4e2. Building UberSplats (Splats\UberSplatData.slk draped over heightMap)
  async function addUnitUberSplat(map, unit) {
    if (!unit || !unit.dooUnit || map._w3sDead) return;
    const id = unit.dooUnit.id;
    if (!id || id === "sloc") return;
    await ensureExtraBaseFiles(map.viewer);
    const splatTable = map.viewer._w3sUberSplatData;
    const uRow = map.unitsData && map.unitsData.getRow(id);
    if (!splatTable || !uRow) return;
    const splatName = uRow.string("uberSplat");
    if (!splatName || splatName === "_" || splatName === "0" || splatName === "NULL") return;

    let sRow = splatTable.getRow(splatName);
    if (!sRow && splatTable.map instanceof Map) {
      const want = splatName.toLowerCase();
      for (const [k, v] of splatTable.map.entries()) {
        if (String(k).toLowerCase() === want) { sRow = v; break; }
      }
    }
    if (!sRow) return;

    const texPath = normalizeAssetPath(`${sRow.string("Dir") || "ReplaceableTextures\\Splats"}\\${sRow.string("file")}.blp`);
    if (!texPath) return;

    const scale = sRow.number("Scale") > 0 ? sRow.number("Scale") : 100;
    const blend = (sRow.number("BlendMode") | 0) === 1 ? 1 : 0;
    const numOr = (col, def) => {
      const v = sRow.number(col);
      return Number.isFinite(v) && (v > 0 || sRow.string(col) !== "") ? v : def;
    };
    const r = numOr("MiddleR", 255) / 255;
    const g = numOr("MiddleG", 255) / 255;
    const b = numOr("MiddleB", 255) / 255;
    const a = numOr("MiddleA", 255) / 255;
    if (a <= 0.005) return;

    const batches = map._w3sSplatBatches || (map._w3sSplatBatches = new Map());
    const key = `${blend}:${texPath.toLowerCase()}`;
    let batch = batches.get(key);
    if (!batch) {
      batch = { texturePath: texPath, texture: null, blend, vertices: [], indices: [], vb: null, ib: null, indexCount: 0, dirty: true };
      batches.set(key, batch);
      map.load(texPath).then((res) => {
        if (batch && res && !map._w3sDead) batch.texture = res;
      });
    }

    const ux = unit.dooUnit.location[0];
    const uy = unit.dooUnit.location[1];
    const grid = 4;
    const baseVert = batch.vertices.length / 8;
    if (baseVert + 25 >= 65535) return;
    for (let iy = 0; iy <= grid; iy++) {
      const fy = iy / grid;
      const py = uy + (fy - 0.5) * 2 * scale;
      for (let ix = 0; ix <= grid; ix++) {
        const fx = ix / grid;
        const px = ux + (fx - 0.5) * 2 * scale;
        batch.vertices.push(px, py, fx, 1 - fy, r, g, b, a);
      }
    }
    const stride = grid + 1;
    for (let iy = 0; iy < grid; iy++) {
      for (let ix = 0; ix < grid; ix++) {
        const v00 = baseVert + iy * stride + ix;
        const v10 = v00 + 1;
        const v01 = v00 + stride;
        const v11 = v01 + 1;
        batch.indices.push(v00, v10, v11, v00, v11, v01);
      }
    }
    batch.dirty = true;
  }

  const origLoadUnits = proto.loadUnitsAndItems;
  proto.loadUnitsAndItems = function () {
    if (!this._w3sUnitsHooked && Array.isArray(this.units)) {
      this._w3sUnitsHooked = true;
      const self = this;
      const origPush = this.units.push.bind(this.units);
      this.units.push = function (...items) {
        for (const item of items) {
          addUnitUberSplat(self, item);
        }
        return origPush(...items);
      };
    }
    return origLoadUnits.apply(this, arguments);
  };

  proto.renderSplats = function () {
    if (!this.terrainReady || !this.heightMap || !this._w3sSplatBatches || this._w3sSplatBatches.size === 0) return;
    const shader = this.viewer.splatShader;
    if (!shader) return;

    const gl = this.viewer.gl;
    const webgl = this.viewer.webgl;
    const u = shader.uniforms;
    const a = shader.attribs;

    gl.disable(gl.CULL_FACE);
    gl.enable(gl.DEPTH_TEST);
    gl.depthMask(false);
    gl.enable(gl.BLEND);
    gl.enable(gl.POLYGON_OFFSET_FILL);
    gl.polygonOffset(-1, -1);

    webgl.useShader(shader);
    gl.uniformMatrix4fv(u.u_VP, false, this.worldScene.camera.viewProjectionMatrix);
    gl.uniform2fv(u.u_offset, this.centerOffset);
    gl.uniform2f(u.u_size, this.columns, this.rows);
    gl.uniform1i(u.u_heightMap, 0);
    gl.uniform1i(u.u_shadowMap, 1);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, this.heightMap);
    gl.activeTexture(gl.TEXTURE1);
    gl.bindTexture(gl.TEXTURE_2D, this.shadowMap);

    const sl = this._w3sSceneLight;
    if (u.u_splatLightTint !== undefined) {
      if (sl) {
        gl.uniform3f(
          u.u_splatLightTint,
          Math.max(0.10, sl.ambient[0] + sl.diffuse[0] * 0.72),
          Math.max(0.10, sl.ambient[1] + sl.diffuse[1] * 0.72),
          Math.max(0.10, sl.ambient[2] + sl.diffuse[2] * 0.72)
        );
      } else {
        gl.uniform3f(u.u_splatLightTint, 1, 1, 1);
      }
    }
    setFogUniforms(gl, u, this._w3sVisuals && this._w3sVisuals.fog);

    for (const batch of this._w3sSplatBatches.values()) {
      if (!batch.texture || batch.indices.length === 0) continue;
      if (batch.dirty) {
        batch.dirty = false;
        if (!batch.vb) batch.vb = gl.createBuffer();
        if (!batch.ib) batch.ib = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, batch.vb);
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(batch.vertices), gl.STATIC_DRAW);
        gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, batch.ib);
        gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, new Uint16Array(batch.indices), gl.STATIC_DRAW);
        batch.indexCount = batch.indices.length;
      }
      if (!batch.indexCount) continue;

      gl.blendFunc(gl.SRC_ALPHA, batch.blend === 1 ? gl.ONE : gl.ONE_MINUS_SRC_ALPHA);
      if (u.u_additive !== undefined) gl.uniform1f(u.u_additive, batch.blend === 1 ? 1 : 0);
      gl.uniform1i(u.u_splatTexture, 2);
      webgl.bindTexture(batch.texture, 2);

      gl.bindBuffer(gl.ARRAY_BUFFER, batch.vb);
      gl.vertexAttribPointer(a.a_position, 2, gl.FLOAT, false, 32, 0);
      gl.vertexAttribPointer(a.a_uv, 2, gl.FLOAT, false, 32, 8);
      gl.vertexAttribPointer(a.a_color, 4, gl.FLOAT, false, 32, 16);
      gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, batch.ib);
      gl.drawElements(gl.TRIANGLES, batch.indexCount, gl.UNSIGNED_SHORT, 0);
    }

    gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
    gl.disable(gl.POLYGON_OFFSET_FILL);
    gl.depthMask(true);
  };

  const origRenderShadows = proto.renderShadows;
  proto.renderShadows = function () {
    this.renderSplats();
    const gl = this.viewer.gl;
    gl.disable(gl.CULL_FACE);
    gl.enable(gl.DEPTH_TEST);
    origRenderShadows.apply(this, arguments);
  };

  const origDie = proto.die;
  proto.die = function () {
    this._w3sDead = true;
    if (Array.isArray(this._w3sShoreInstances)) {
      for (const inst of this._w3sShoreInstances) {
        try { inst.setScene(null); } catch {}
      }
      this._w3sShoreInstances.length = 0;
    }
    if (this._w3sSplatBatches instanceof Map) {
      const gl = this.viewer && this.viewer.gl;
      for (const batch of this._w3sSplatBatches.values()) {
        if (gl) {
          if (batch.vb) gl.deleteBuffer(batch.vb);
          if (batch.ib) gl.deleteBuffer(batch.ib);
        }
      }
      this._w3sSplatBatches.clear();
    }
    return origDie.apply(this, arguments);
  };

  const origUpdate = proto.update;
  proto.update = function () {
    origUpdate.apply(this, arguments);
    if (this.anyReady && this._waterUvOffset && this._waterScrollRate) {
      this._waterUvOffset[0] = (this._waterUvOffset[0] + this._waterScrollRate[0] * (1 / 60)) % 1.0;
      this._waterUvOffset[1] = (this._waterUvOffset[1] + this._waterScrollRate[1] * (1 / 60)) % 1.0;
    }
  };

  // 4f. Avoid doubled static building/doodad shadows when war3map.shd is already present,
  // clamp oversized ShadowFlyer decals, and skip sentinel invalid shadow names.
  const origAddShadowDecal = proto.addShadowDecal;
  proto.addShadowDecal = function (texPath, x, y, w, h, alpha) {
    const cleaned = normalizeAssetPath(texPath);
    if (!cleaned) return;
    // Moving units pass alpha === 0.62; static buildings pass 0.48 and doodads pass <= 0.46.
    // When war3map.shd exists, static building/doodad shadows are already baked cleanly on terrain.
    if (this._mapHasShdFile && alpha <= 0.5) {
      return;
    }
    const clampedW = Math.min(w, 512);
    const clampedH = Math.min(h, 512);
    return origAddShadowDecal.call(this, cleaned, x, y, clampedW, clampedH, alpha);
  };

  console.info("[W3Studio Fixes] Warcraft III 1.27b faithful viewport patches installed.");
})();

