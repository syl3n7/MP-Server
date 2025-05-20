// 3D Viewer for Racing Server Dashboard
let scene, camera, renderer, mapModel;
let playerModels = {};
const updateInterval = 1000; // Update player positions every second
let currentRoomId = null;
let animationFrame = null;

// Initialize the 3D viewer
function initViewer(containerId, roomId) {
    currentRoomId = roomId;
    const container = document.getElementById(containerId);
    const width = container.clientWidth;
    const height = container.clientHeight;
    
    // Create scene
    scene = new THREE.Scene();
    scene.background = new THREE.Color(0x87ceeb); // Sky blue background
    
    // Add lighting
    const ambientLight = new THREE.AmbientLight(0xffffff, 0.6);
    scene.add(ambientLight);
    
    const directionalLight = new THREE.DirectionalLight(0xffffff, 0.8);
    directionalLight.position.set(0, 10, 10);
    scene.add(directionalLight);
    
    // Camera setup
    camera = new THREE.PerspectiveCamera(75, width / height, 0.1, 1000);
    camera.position.set(0, 50, 100);
    camera.lookAt(0, 0, 0);
    
    // Add simple ground plane until map is loaded
    const groundGeo = new THREE.PlaneGeometry(200, 200);
    const groundMat = new THREE.MeshStandardMaterial({
        color: 0x3c763d,
        side: THREE.DoubleSide
    });
    const ground = new THREE.Mesh(groundGeo, groundMat);
    ground.rotation.x = -Math.PI / 2;
    scene.add(ground);
    
    // Renderer setup
    renderer = new THREE.WebGLRenderer({ antialias: true });
    renderer.setSize(width, height);
    container.appendChild(renderer.domElement);
    
    // Add orbit controls
    const controls = new THREE.OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    controls.dampingFactor = 0.05;
    
    // Start animation loop
    animate();
    
    // Start polling for player updates
    updatePlayerPositions();
    
    // Handle window resize
    window.addEventListener('resize', () => {
        const width = container.clientWidth;
        const height = container.clientHeight;
        camera.aspect = width / height;
        camera.updateProjectionMatrix();
        renderer.setSize(width, height);
    });
}

// Animation loop
function animate() {
    animationFrame = requestAnimationFrame(animate);
    renderer.render(scene, camera);
}

// Load 3D map model
function loadMapModel(modelUrl) {
    // Remove any existing map model
    if (mapModel) {
        scene.remove(mapModel);
    }
    
    // Use THREE.js loader based on file type
    if (modelUrl.endsWith('.glb') || modelUrl.endsWith('.gltf')) {
        const loader = new THREE.GLTFLoader();
        loader.load(modelUrl, (gltf) => {
            mapModel = gltf.scene;
            mapModel.scale.set(1, 1, 1); // Adjust scale as needed
            scene.add(mapModel);
        }, undefined, (error) => {
            console.error('Error loading map model:', error);
        });
    } else if (modelUrl.endsWith('.obj')) {
        const loader = new THREE.OBJLoader();
        loader.load(modelUrl, (obj) => {
            mapModel = obj;
            mapModel.scale.set(1, 1, 1); // Adjust scale as needed
            scene.add(mapModel);
        }, undefined, (error) => {
            console.error('Error loading map model:', error);
        });
    } else if (modelUrl.endsWith('.fbx')) {
        const loader = new THREE.FBXLoader();
        loader.load(modelUrl, (obj) => {
            mapModel = obj;
            mapModel.scale.set(0.1, 0.1, 0.1); // FBX models often need scaling
            scene.add(mapModel);
        }, undefined, (error) => {
            console.error('Error loading map model:', error);
        });
    }
}

// Update player positions from API
async function updatePlayerPositions() {
    if (!currentRoomId) return;
    
    try {
        const response = await fetch(`/api/RoomData/room/${currentRoomId}/players`);
        
        if (response.ok) {
            const players = await response.json();
            
            // Update or create player models
            players.forEach(player => {
                if (!playerModels[player.id]) {
                    // Create new player model
                    const geometry = new THREE.BoxGeometry(2, 1, 4); // Simple car shape
                    const material = new THREE.MeshLambertMaterial({
                        color: getRandomColor()
                    });
                    const playerObj = new THREE.Mesh(geometry, material);
                    
                    // Add player name as text above the car
                    const textGeo = new THREE.TextGeometry(player.name, {
                        font: null, // Need to load a font first
                        size: 0.5,
                        height: 0.1
                    });
                    const textMat = new THREE.MeshBasicMaterial({ color: 0xffffff });
                    const textMesh = new THREE.Mesh(textGeo, textMat);
                    textMesh.position.set(0, 2, 0);
                    playerObj.add(textMesh);
                    
                    playerModels[player.id] = playerObj;
                    scene.add(playerObj);
                }
                
                // Update player position and rotation
                const model = playerModels[player.id];
                
                // Update position directly since TWEEN might not be available
                model.position.set(
                    player.position.x,
                    player.position.y,
                    player.position.z
                );
                
                // Apply rotation
                model.quaternion.set(
                    player.rotation.x,
                    player.rotation.y,
                    player.rotation.z,
                    player.rotation.w
                );
            });
            
            // Remove players that are no longer in the room
            Object.keys(playerModels).forEach(id => {
                if (!players.find(p => p.id === id)) {
                    scene.remove(playerModels[id]);
                    delete playerModels[id];
                }
            });
        }
    } catch (err) {
        console.error('Error fetching player positions:', err);
    }
    
    // Schedule next update
    setTimeout(updatePlayerPositions, updateInterval);
}

// Generate random color for player models
function getRandomColor() {
    return Math.random() * 0xffffff;
}

// Clean up when changing rooms or closing viewer
function cleanupViewer() {
    if (animationFrame) {
        cancelAnimationFrame(animationFrame);
    }
    
    // Remove all player models
    Object.values(playerModels).forEach(model => {
        scene.remove(model);
    });
    playerModels = {};
    
    // Remove map model
    if (mapModel) {
        scene.remove(mapModel);
        mapModel = null;
    }
    
    // Clear scene
    while(scene.children.length > 0){ 
        scene.remove(scene.children[0]); 
    }
    
    currentRoomId = null;
}