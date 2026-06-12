const API_URL = ''; // Relative path

// State
let jobs = [];
let isUploading = false;

// DOM Elements
const jobListEl = document.getElementById('jobList');
const uploadZone = document.getElementById('uploadZone');
const fileInput = document.getElementById('fileInput');
const webcamImg = document.getElementById('webcamImg');

// Initialization
document.addEventListener('DOMContentLoaded', init);

async function init() {
    await fetchJobs();
    await fetchProfiles();
    setupUpload();
    setupSliders();

    // Auto-refresh queue every 5 seconds
    setInterval(fetchJobs, 5000); // Slower refresh for queue

    // Poll Status (Progress)
    setInterval(fetchStatus, 1000);

    // Refresh Webcam
    setInterval(() => {
        // Simple cache bust for snapshot if stream is not used
        // webcamImg.src = `/webcam/snapshot?t=${Date.now()}`;
    }, 5000);
}

async function fetchStatus() {
    try {
        const res = await fetch(`${API_URL}/printer/status`);
        if (res.ok) {
            const status = await res.json();
            document.getElementById('printerStatus').innerText = status.isPrinting ? 'Printing' : 'Idle';

            document.getElementById('progressBar').value = status.progress;
            document.getElementById('progressText').innerText = `${status.progress}%`;

            // Lock controls if printing (Optional, Safety Interlock enforces it backend-side, but UX is good)
            const controls = document.querySelectorAll('#preheat-profiles button, #movement-controls button'); // Need ID on movement controls container if we want to disable
        }
    } catch (e) {
        // ignore
    }
}

function setupSliders() {
    const speed = document.getElementById('speedSlider');
    const flow = document.getElementById('flowSlider');

    speed.oninput = () => {
        document.getElementById('speedLabel').innerText = `Speed: ${100 + parseInt(speed.value)}%`;
    };
    speed.onchange = () => {
        const val = 100 + parseInt(speed.value);
        sendCommand(`M220 S${val}`);
    };

    flow.oninput = () => {
        document.getElementById('flowLabel').innerText = `Flow: ${100 + parseInt(flow.value)}%`;
    };
    flow.onchange = () => {
        const val = 100 + parseInt(flow.value);
        sendCommand(`M221 S${val}`);
    };
}

async function fetchJobs() {
    try {
        const res = await fetch(`${API_URL}/jobs`);
        if (!res.ok) throw new Error('Failed to fetch jobs');
        jobs = await res.json();
        renderJobs();
    } catch (err) {
        console.error(err);
    }
}

function renderJobs() {
    jobListEl.innerHTML = '';

    if (jobs.length === 0) {
        jobListEl.innerHTML = '<li class="job-item" style="justify-content:center; color:#555;">No jobs queued</li>';
        return;
    }

    jobs.forEach(job => {
        const li = document.createElement('li');
        li.className = 'job-item';

        let actions = '';
        if (job.status === 'Queued') {
            actions += `<button class="icon-btn" onclick="startJob('${job.id}')" title="Start">▶</button>`;
        }
        if (job.status === 'Printing') {
            actions += `<button class="icon-btn" onclick="cancelJob('${job.id}')" title="Cancel" style="color:var(--danger-color)">⏹</button>`;
        }
        actions += `<button class="icon-btn" onclick="deleteJob('${job.id}')" title="Delete">🗑</button>`;

        li.innerHTML = `
            <div class="job-info">
                <span class="job-name">${job.name}</span>
                <span class="job-status" style="color:${getStatusColor(job.status)}">${job.status}</span>
            </div>
            <div class="job-actions">
                ${actions}
            </div>
        `;
        jobListEl.appendChild(li);
    });
}

function getStatusColor(status) {
    switch (status) {
        case 'Printing': return 'var(--accent-color)';
        case 'Completed': return 'var(--success-color)';
        case 'Failed': return 'var(--danger-color)';
        default: return 'var(--text-secondary)';
    }
}

async function startJob(id) {
    await fetch(`${API_URL}/jobs/${id}/start`, { method: 'POST' });
    fetchJobs();
}

async function cancelJob(id) {
    await fetch(`${API_URL}/jobs/${id}/cancel`, { method: 'POST' });
    fetchJobs();
}

async function deleteJob(id) {
    if (!confirm('Delete this job?')) return;
    await fetch(`${API_URL}/jobs/${id}`, { method: 'DELETE' });
    fetchJobs();
}

// Upload Logic
function setupUpload() {
    uploadZone.addEventListener('click', () => fileInput.click());

    uploadZone.addEventListener('dragover', (e) => {
        e.preventDefault();
        uploadZone.style.borderColor = 'var(--accent-color)';
    });

    uploadZone.addEventListener('dragleave', () => {
        uploadZone.style.borderColor = '#444';
    });

    uploadZone.addEventListener('drop', (e) => {
        e.preventDefault();
        uploadZone.style.borderColor = '#444';
        if (e.dataTransfer.files.length) {
            handleUpload(e.dataTransfer.files[0]);
        }
    });

    fileInput.addEventListener('change', () => {
        if (fileInput.files.length) {
            handleUpload(fileInput.files[0]);
        }
    });
}

async function handleUpload(file) {
    if (isUploading) return;
    isUploading = true;
    uploadZone.innerText = 'Uploading...';

    const formData = new FormData();
    formData.append('file', file);

    try {
        const res = await fetch(`${API_URL}/jobs/upload`, {
            method: 'POST',
            body: formData
        });

        if (!res.ok) throw new Error('Upload failed');
        uploadZone.innerText = 'Upload Successful!';
        setTimeout(() => uploadZone.innerText = 'Drop G-Code here or Click', 2000);
        fetchJobs();
    } catch (err) {
        uploadZone.innerText = 'Error!';
        console.error(err);
    } finally {
        isUploading = false;
        fileInput.value = '';
    }
}

// Printer Controls
async function sendCommand(gcode) {
    try {
        console.log('Sending:', gcode);
        const res = await fetch(`${API_URL}/printer/command`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ command: gcode })
        });

        if (!res.ok) {
            const err = await res.json();
            alert(`Error: ${err.error || 'Command Failed'}`);
        }
    } catch (e) {
        console.error(e);
        document.getElementById('printerStatus').innerText = 'Offline';
    }
}

async function fetchProfiles() {
    try {
        const res = await fetch(`${API_URL}/printer/profiles`);
        if (res.ok) {
            const profiles = await res.json();
            const container = document.getElementById('preheat-profiles');
            container.innerHTML = '';

            profiles.forEach(p => {
                const btn = document.createElement('button');
                btn.className = 'btn';
                btn.textContent = `${p.name} (${p.extruderTemp}/${p.bedTemp})`;
                btn.onclick = () => {
                    document.getElementById('extTemp').value = p.extruderTemp;
                    document.getElementById('bedTemp').value = p.bedTemp;
                    setTemp('M104', 'extTemp'); // Send Ext
                    setTemp('M140', 'bedTemp'); // Send Bed
                };
                container.appendChild(btn);
            });
        }
    } catch (e) {
        console.error("Failed to fetch profiles", e);
    }
}

async function startJob(id) {
    await fetch(`${API_URL}/jobs/${id}/start`, { method: 'POST' });
    fetchJobs();
}

async function cancelJob(id) {
    await fetch(`${API_URL}/jobs/${id}/cancel`, { method: 'POST' });
    fetchJobs();
}

async function deleteJob(id) {
    if (!confirm('Delete this job?')) return;
    await fetch(`${API_URL}/jobs/${id}`, { method: 'DELETE' });
    fetchJobs();
}

async function moveAxis(axis, amount) {
    // Relative movement sequence
    // G91 (Relative) -> G0 AxisAmount F3000 -> G90 (Absolute)
    // We send them as separate commands for simplicity with our API,
    // though sending a newline-separated block would be better if the API supported it (it does line-by-line).
    // Our current SerialService sends line-by-line and waits for 'ok'.

    // NOTE: If we send G91, then G1, then G90 distinct calls, another thread *could* interject.
    // For a robust system, we should have a "Block" command, or just send one string with \n.
    // Our backend splits by line, so let's send one block if possible.
    // The current JobProcessor handles jobs, but /printer/command sends single lines? 
    // Let's check SerialConnectionService... 
    // It takes "string gcode". If it contains \n, SerialPort might send it all at once or separate?
    // User plan said "SendGCodeLineAsync". Most firmwares accept multiple commands if separated? 
    // NO, usually one per line, wait for ok.
    // So to be safe, we will just send one command: "G91 G1 X10 F3000 G90" on one line? No, G-Code doesn't standardly support that on all firmwares.

    // SAFE APPROACH: Send G91, then Move, then G90. 
    // Risk: If G90 fails to send, printer stays in relative.

    await sendCommand('G91');
    await sendCommand(`G1 ${axis}${amount} F3000`);
    await sendCommand('G90');
}

function setTemp(code, inputId) {
    const val = document.getElementById(inputId).value;
    if (val) {
        sendCommand(`${code} S${val}`);
    }
}
