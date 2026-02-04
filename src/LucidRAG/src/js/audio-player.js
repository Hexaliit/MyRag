// Audio Player with Wavesurfer.js integration for LucidRAG
// Displays audio evidence with interactive waveforms

import WaveSurfer from 'wavesurfer.js'

/**
 * Audio Player Manager for evidence display
 * Supports single tracks and multi-track stem playback
 */
class AudioPlayerManager {
    constructor() {
        this.players = new Map() // Track active wavesurfer instances by container ID
        this.activePlayerId = null
    }

    /**
     * Create a single-track audio player
     * @param {string} containerId - DOM element ID for the player
     * @param {string} audioUrl - URL to the audio file
     * @param {Object} options - Player options
     */
    createPlayer(containerId, audioUrl, options = {}) {
        // Destroy existing player in this container
        if (this.players.has(containerId)) {
            this.players.get(containerId).destroy()
        }

        const container = document.getElementById(containerId)
        if (!container) {
            console.error(`Container ${containerId} not found`)
            return null
        }

        const defaultOptions = {
            container: container,
            waveColor: 'rgb(107, 114, 128)',
            progressColor: 'rgb(139, 92, 246)',
            cursorColor: 'rgb(168, 85, 247)',
            barWidth: 2,
            barGap: 1,
            barRadius: 2,
            height: options.height || 64,
            responsive: true,
            normalize: true,
            backend: 'WebAudio',
            hideScrollbar: true
        }

        const wavesurfer = WaveSurfer.create({
            ...defaultOptions,
            ...options
        })

        // Load audio
        wavesurfer.load(audioUrl)

        // Handle events
        wavesurfer.on('ready', () => {
            const duration = wavesurfer.getDuration()
            const timeEl = container.parentElement?.querySelector('.audio-duration')
            if (timeEl) {
                timeEl.textContent = this.formatTime(duration)
            }
        })

        wavesurfer.on('audioprocess', () => {
            const current = wavesurfer.getCurrentTime()
            const timeEl = container.parentElement?.querySelector('.audio-current-time')
            if (timeEl) {
                timeEl.textContent = this.formatTime(current)
            }
        })

        wavesurfer.on('play', () => {
            // Pause other players when this one starts
            this.pauseAllExcept(containerId)
            this.activePlayerId = containerId
        })

        wavesurfer.on('error', (err) => {
            console.error(`Wavesurfer error for ${containerId}:`, err)
            container.innerHTML = `<div class="text-error text-sm">Failed to load audio</div>`
        })

        this.players.set(containerId, wavesurfer)
        return wavesurfer
    }

    /**
     * Create a multi-track stem player
     * @param {string} containerId - Container for the stem player
     * @param {Object} stems - Object with stem URLs {vocals, drums, bass, other, instrumentals}
     */
    createStemPlayer(containerId, stems) {
        const container = document.getElementById(containerId)
        if (!container) {
            console.error(`Container ${containerId} not found`)
            return null
        }

        // Create individual track containers
        const stemPlayers = {}
        const stemColors = {
            vocals: {wave: 'rgb(236, 72, 153)', progress: 'rgb(219, 39, 119)'},      // Pink
            drums: {wave: 'rgb(245, 158, 11)', progress: 'rgb(217, 119, 6)'},        // Amber
            bass: {wave: 'rgb(16, 185, 129)', progress: 'rgb(5, 150, 105)'},         // Green
            other: {wave: 'rgb(59, 130, 246)', progress: 'rgb(37, 99, 235)'},        // Blue
            instrumentals: {wave: 'rgb(139, 92, 246)', progress: 'rgb(124, 58, 237)'} // Purple
        }

        const stemLabels = {
            vocals: 'Vocals',
            drums: 'Drums',
            bass: 'Bass',
            other: 'Melody',
            instrumentals: 'Instrumentals'
        }

        // Build UI
        container.innerHTML = `
            <div class="stem-player bg-base-200 rounded-lg p-3 space-y-2">
                <div class="flex items-center justify-between mb-2">
                    <span class="text-sm font-medium">Source Separation</span>
                    <div class="flex gap-2">
                        <button class="btn btn-xs btn-ghost stem-play-all" title="Play All">
                            <svg class="w-4 h-4" fill="currentColor" viewBox="0 0 24 24">
                                <path d="M8 5v14l11-7z"/>
                            </svg>
                        </button>
                        <button class="btn btn-xs btn-ghost stem-pause-all" title="Pause All">
                            <svg class="w-4 h-4" fill="currentColor" viewBox="0 0 24 24">
                                <path d="M6 19h4V5H6v14zm8-14v14h4V5h-4z"/>
                            </svg>
                        </button>
                    </div>
                </div>
                <div class="stem-tracks space-y-2"></div>
            </div>
        `

        const tracksContainer = container.querySelector('.stem-tracks')

        // Create track for each stem
        Object.entries(stems).forEach(([stemType, url]) => {
            if (!url) return

            const colors = stemColors[stemType] || stemColors.other
            const label = stemLabels[stemType] || stemType

            const trackEl = document.createElement('div')
            trackEl.className = 'stem-track flex items-center gap-2'
            trackEl.innerHTML = `
                <div class="w-20 flex items-center gap-1">
                    <button class="btn btn-xs btn-circle stem-mute" data-stem="${stemType}" title="Mute">
                        <svg class="w-3 h-3" fill="currentColor" viewBox="0 0 24 24">
                            <path d="M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02z"/>
                        </svg>
                    </button>
                    <span class="text-xs">${label}</span>
                </div>
                <div id="${containerId}-${stemType}" class="flex-1 h-10"></div>
            `
            tracksContainer.appendChild(trackEl)

            // Create wavesurfer for this stem
            const ws = WaveSurfer.create({
                container: trackEl.querySelector(`#${containerId}-${stemType}`),
                waveColor: colors.wave,
                progressColor: colors.progress,
                cursorColor: 'rgb(168, 85, 247)',
                barWidth: 1,
                barGap: 1,
                height: 40,
                responsive: true,
                normalize: true,
                interact: false, // Disable direct interaction, sync through master
            })

            ws.load(url)
            stemPlayers[stemType] = ws

            // Mute button
            const muteBtn = trackEl.querySelector('.stem-mute')
            let isMuted = false
            muteBtn.addEventListener('click', () => {
                isMuted = !isMuted
                ws.setMuted(isMuted)
                muteBtn.classList.toggle('btn-error', isMuted)
            })
        })

        // Play/Pause all controls
        container.querySelector('.stem-play-all').addEventListener('click', () => {
            Object.values(stemPlayers).forEach(ws => ws.play())
        })

        container.querySelector('.stem-pause-all').addEventListener('click', () => {
            Object.values(stemPlayers).forEach(ws => ws.pause())
        })

        // Sync playback position across all stems
        const stemList = Object.values(stemPlayers)
        if (stemList.length > 0) {
            const master = stemList[0]
            master.on('seek', () => {
                const progress = master.getCurrentTime() / master.getDuration()
                stemList.slice(1).forEach(ws => ws.seekTo(progress))
            })
        }

        this.players.set(containerId, stemPlayers)
        return stemPlayers
    }

    /**
     * Pause all players except the specified one
     */
    pauseAllExcept(exceptId) {
        this.players.forEach((player, id) => {
            if (id !== exceptId) {
                if (player.pause) {
                    player.pause()
                } else if (typeof player === 'object') {
                    // Stem player - pause all tracks
                    Object.values(player).forEach(ws => ws.pause())
                }
            }
        })
    }

    /**
     * Get player by container ID
     */
    getPlayer(containerId) {
        return this.players.get(containerId)
    }

    /**
     * Destroy player and clean up
     */
    destroyPlayer(containerId) {
        const player = this.players.get(containerId)
        if (player) {
            if (player.destroy) {
                player.destroy()
            } else if (typeof player === 'object') {
                Object.values(player).forEach(ws => ws.destroy())
            }
            this.players.delete(containerId)
        }
    }

    /**
     * Destroy all players
     */
    destroyAll() {
        this.players.forEach((_, id) => this.destroyPlayer(id))
    }

    /**
     * Format seconds to mm:ss
     */
    formatTime(seconds) {
        const mins = Math.floor(seconds / 60)
        const secs = Math.floor(seconds % 60)
        return `${mins}:${secs.toString().padStart(2, '0')}`
    }
}

// Create global instance
const audioPlayer = new AudioPlayerManager()

// Export for module use
export {audioPlayer, AudioPlayerManager}

// Make available globally for inline scripts
window.AudioPlayer = audioPlayer
