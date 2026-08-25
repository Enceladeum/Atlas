// Three.js viewport wrapper for territory scenes.
// Loads the composed-map glTF (layer groups preserved, identity quadruple in
// node extras -> userData) and the collision OBJ as an overlay. Coordinates come
// through unmodified from Atlas.Core (verified against the Pcb OBJ path).
import * as THREE from "three";
import { OrbitControls } from "../vendor/jsm/controls/OrbitControls.js";
import { GLTFLoader } from "../vendor/jsm/loaders/GLTFLoader.js";
import { OBJLoader } from "../vendor/jsm/loaders/OBJLoader.js";

export class Viewport {
  constructor(container, { onSelect } = {}) {
    this.container = container;
    this.onSelect = onSelect;
    this.renderer = new THREE.WebGLRenderer({ antialias: true, powerPreference: "high-performance" });
    this.renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
    this.renderer.domElement.style.display = "block";
    container.append(this.renderer.domElement);

    this.scene = new THREE.Scene();
    this.scene.background = new THREE.Color(0x0b0d10);
    this.scene.fog = new THREE.Fog(0x0b0d10, 600, 2400);

    this.camera = new THREE.PerspectiveCamera(55, 1, 0.5, 6000);
    this.camera.position.set(120, 140, 120);

    this.controls = new OrbitControls(this.camera, this.renderer.domElement);
    this.controls.enableDamping = true;
    this.controls.dampingFactor = 0.08;

    // Wheel zoom: OrbitControls' dolly multiplies distance-to-target by a
    // constant factor, so approach speed decays to zero exactly when you want
    // to inspect detail. Replaced: translate the camera along the ray through
    // the cursor pixel with a floored step (never stalls, passes through
    // surfaces), then re-seat the orbit pivot ahead of the camera. Orientation
    // is untouched, so the point under the cursor stays under the cursor.
    this.controls.enableZoom = false;
    this._wheel = (e) => {
      e.preventDefault();
      const r = this.renderer.domElement.getBoundingClientRect();
      const ndc = new THREE.Vector2(
        ((e.clientX - r.left) / r.width) * 2 - 1,
        -((e.clientY - r.top) / r.height) * 2 + 1);
      this._raycaster.setFromCamera(ndc, this.camera);
      const dir = this._raycaster.ray.direction;
      const dist = this.camera.position.distanceTo(this.controls.target);
      const step = Math.max(dist * 0.22, 2.5) * (e.deltaY < 0 ? 1 : -1.25);
      this.camera.position.addScaledVector(dir, step);
      const view = new THREE.Vector3();
      this.camera.getWorldDirection(view);
      this.controls.target.copy(this.camera.position)
        .addScaledVector(view, Math.max(dist - step, 6));
    };
    this.renderer.domElement.addEventListener("wheel", this._wheel, { passive: false });

    const hemi = new THREE.HemisphereLight(0xcfd8e3, 0x2a2620, 1.05);
    const dir = new THREE.DirectionalLight(0xfff2d8, 1.4);
    dir.position.set(0.6, 1, 0.35);
    this.scene.add(hemi, dir);

    const grid = new THREE.GridHelper(1000, 50, 0x232936, 0x161a21);
    grid.material.transparent = true; grid.material.opacity = 0.5;
    this.scene.add(grid);

    this.mapRoot = null;        // glTF scene (visual)
    this.collisionRoot = null;  // OBJ mesh group (overlay)
    this._selMat = new THREE.MeshBasicMaterial({ color: 0xe8b04b, wireframe: true });
    this._selMesh = null;
    this._raycaster = new THREE.Raycaster();

    this._resize = () => {
      const w = container.clientWidth, h = container.clientHeight;
      if (!w || !h) return;
      this.renderer.setSize(w, h, false);
      this.camera.aspect = w / h;
      this.camera.updateProjectionMatrix();
    };
    this._ro = new ResizeObserver(this._resize);
    this._ro.observe(container);
    this._resize();

    this.renderer.domElement.addEventListener("pointerdown", (e) => {
      this._down = [e.clientX, e.clientY, performance.now()];
    });
    this.renderer.domElement.addEventListener("pointerup", (e) => {
      if (!this._down) return;
      const [x, y, t] = this._down; this._down = null;
      if (Math.hypot(e.clientX - x, e.clientY - y) > 4 || performance.now() - t > 400) return; // drag, not click
      this._pick(e);
    });

    this._alive = true;
    const loop = () => {
      if (!this._alive) return;
      requestAnimationFrame(loop);
      this.controls.update();
      if (this._marker) {
        const t = (performance.now() - this._markerT0) / 1000;
        this._marker.rotation.y = t * 2.2;
        this._marker.scale.setScalar(this._markerBase * (1 + 0.25 * Math.sin(t * 5)));
        if (t > 8) this._clearMarker();
      }
      this.renderer.render(this.scene, this.camera);
    };
    loop();
  }

  async loadMapGltf(url, onProgress) {
    this.clearMap();
    const gltf = await new Promise((res, rej) =>
      new GLTFLoader().load(url, res, (ev) => onProgress?.(ev.loaded), rej));
    this.mapRoot = gltf.scene;
    // double-sided: bg geometry has plenty of single-sided faces viewed from behind
    this.mapRoot.traverse(o => { if (o.isMesh) { o.material.side = THREE.DoubleSide; } });
    this.scene.add(this.mapRoot);
    this.fit(this.mapRoot);
    return this.layers();
  }

  async loadCollisionObj(url, onProgress) {
    this.clearCollision();
    const text = await (await fetch(url)).text();
    onProgress?.(text.length);
    const obj = new OBJLoader().parse(text);
    const mat = new THREE.MeshBasicMaterial({
      color: 0x3ba55f, wireframe: true, transparent: true, opacity: 0.28, depthWrite: false,
    });
    obj.traverse(o => { if (o.isMesh) o.material = mat; });
    this.collisionRoot = obj;
    this.scene.add(obj);
    if (!this.mapRoot) this.fit(obj);
    return obj;
  }

  // shaded OBJ preview (asset browser): neutral material, no wireframe
  async loadObjShaded(url) {
    this.clearMap();
    const text = await (await fetch(url)).text();
    if (!/^(#|v |o |g )/m.test(text)) throw new Error("not an OBJ (server error?)");
    const obj = new OBJLoader().parse(text);
    const mat = new THREE.MeshStandardMaterial({ color: 0xb9bec7, roughness: 0.85, metalness: 0.05, side: THREE.DoubleSide });
    obj.traverse(o => { if (o.isMesh) o.material = mat; });
    this.mapRoot = obj;
    this.scene.add(obj);
    this.fit(obj);
    return obj;
  }

  clearMap() { if (this.mapRoot) { this.scene.remove(this.mapRoot); disposeDeep(this.mapRoot); this.mapRoot = null; } this._clearSel(); this._clearMarker(); }
  clearCollision() { if (this.collisionRoot) { this.scene.remove(this.collisionRoot); disposeDeep(this.collisionRoot); this.collisionRoot = null; } }

  layers() {
    if (!this.mapRoot) return [];
    // compose writer: scene -> single "map-<tt>" root -> one group per LGB layer
    let host = this.mapRoot;
    while (host.children.length === 1 && host.children[0].children.length) host = host.children[0];
    return host.children.map(g => ({
      name: g.name || "(unnamed)",
      count: g.children.length,
      visible: g.visible,
      setVisible: (v) => { g.visible = v; },
    }));
  }

  setMapVisible(v) { if (this.mapRoot) this.mapRoot.visible = v; }
  setCollisionVisible(v) { if (this.collisionRoot) this.collisionRoot.visible = v; }

  fit(root) {
    const box = new THREE.Box3().setFromObject(root);
    if (box.isEmpty()) return;
    const c = box.getCenter(new THREE.Vector3());
    const s = box.getSize(new THREE.Vector3()).length();
    this.controls.target.copy(c);
    this.camera.position.copy(c).add(new THREE.Vector3(s * 0.35, s * 0.3, s * 0.35));
    this.camera.near = Math.max(0.1, s / 5000);
    this.camera.far = s * 6;
    this.camera.updateProjectionMatrix();
    this.scene.fog.near = s * 0.9; this.scene.fog.far = s * 3;
  }

  // fly the camera to a world-space point and drop a temporary beacon there
  flyTo(x, y, z, dist = 45) {
    const p = new THREE.Vector3(x, y, z);
    this.controls.target.copy(p);
    this.camera.position.copy(p).add(new THREE.Vector3(1, 0.8, 1).normalize().multiplyScalar(dist));
    this._clearMarker();
    const mat = new THREE.MeshBasicMaterial({ color: 0xd7a557, transparent: true, opacity: 0.9, depthTest: false });
    const m = new THREE.Mesh(new THREE.OctahedronGeometry(1), mat);
    m.position.copy(p);
    m.renderOrder = 999;
    this._marker = m;
    this._markerBase = dist / 30;
    this._markerT0 = performance.now();
    this.scene.add(m);
  }
  _clearMarker() {
    if (!this._marker) return;
    this.scene.remove(this._marker);
    this._marker.geometry.dispose(); this._marker.material.dispose();
    this._marker = null;
  }

  _pick(e) {
    if (!this.mapRoot || !this.mapRoot.visible) return;
    const r = this.renderer.domElement.getBoundingClientRect();
    const p = new THREE.Vector2(((e.clientX - r.left) / r.width) * 2 - 1, -((e.clientY - r.top) / r.height) * 2 + 1);
    this._raycaster.setFromCamera(p, this.camera);
    const hits = this._raycaster.intersectObject(this.mapRoot, true);
    const hit = hits.find(h => h.object.isMesh && h.object.visible);
    if (!hit) { this._clearSel(); this.onSelect?.(null); return; }
    // walk up to the node that carries the identity extras
    let n = hit.object;
    while (n && n !== this.mapRoot && !(n.userData && n.userData.instanceId !== undefined)) n = n.parent;
    const node = (n && n !== this.mapRoot) ? n : hit.object;
    this._select(hit.object);
    this.onSelect?.({
      name: node.name || hit.object.name,
      extras: { ...node.userData },
      point: hit.point,
      focus: () => { this.controls.target.copy(hit.point); },
    });
  }

  _select(mesh) {
    this._clearSel();
    this._selMesh = new THREE.Mesh(mesh.geometry, this._selMat);
    mesh.updateWorldMatrix(true, false);
    this._selMesh.applyMatrix4(mesh.matrixWorld);
    this.scene.add(this._selMesh);
  }
  _clearSel() { if (this._selMesh) { this.scene.remove(this._selMesh); this._selMesh = null; } }

  stats() {
    const i = this.renderer.info;
    return { calls: i.render.calls, tris: i.render.triangles };
  }

  dispose() {
    this._alive = false;
    this._ro.disconnect();
    this.clearMap(); this.clearCollision();
    this.controls.dispose();
    this.renderer.dispose();
    this.renderer.domElement.remove();
  }
}

function disposeDeep(root) {
  root.traverse(o => {
    if (o.isMesh) {
      o.geometry?.dispose();
      const m = o.material;
      (Array.isArray(m) ? m : [m]).forEach(x => x?.dispose?.());
    }
  });
}
