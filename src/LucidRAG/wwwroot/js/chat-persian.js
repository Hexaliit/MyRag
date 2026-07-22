/**
 * لوسیدراگ | اسکریپت صفحه چت فارسی
 * Persian Chat Interface for LucidRAG
 */

(function () {
    'use strict';

    // ============================================================
    // DOM References
    // ============================================================
    const $ = (sel, ctx) => (ctx || document).querySelector(sel);
    const $$ = (sel, ctx) => (ctx || document).querySelectorAll(sel);

    const dom = {
        themeToggle: $('#themeToggle'),
        newChatBtn: $('#newChatBtn'),
        welcomeScreen: $('#welcomeScreen'),
        chatMessages: $('#chatMessages'),
        typingIndicator: $('#typingIndicator'),
        messageInput: $('#messageInput'),
        sendButton: $('#sendButton'),
        stopButton: $('#stopButton'),
        errorToast: $('#errorToast'),
        errorMessage: $('#errorMessage'),
    };

    // ============================================================
    // State
    // ============================================================
    const state = {
        isStreaming: false,
        abortController: null,
        currentAssistantMsg: null,
        currentAssistantBubble: null,
        hasConversation: false,
    };

    // ============================================================
    // Theme Management
    // ============================================================
    function initTheme() {
        const saved = localStorage.getItem('chat-theme') || 'light';
        document.documentElement.setAttribute('data-theme', saved);
    }

    function toggleTheme() {
        const html = document.documentElement;
        const current = html.getAttribute('data-theme');
        const next = current === 'dark' ? 'light' : 'dark';
        html.setAttribute('data-theme', next);
        localStorage.setItem('chat-theme', next);
    }

    // ============================================================
    // Textarea Auto-resize
    // ============================================================
    function autoResizeTextarea() {
        const ta = dom.messageInput;
        ta.style.height = 'auto';
        const maxH = 200;
        const newH = Math.min(ta.scrollHeight, maxH);
        ta.style.height = newH + 'px';
    }

    // ============================================================
    // Scroll Management
    // ============================================================
    function scrollToBottom(smooth) {
        const main = document.querySelector('.chat-main');
        if (!main) return;
        main.scrollTo({
            top: main.scrollHeight,
            behavior: smooth ? 'smooth' : 'auto',
        });
    }

    // ============================================================
    // Markdown Rendering
    // ============================================================
    function renderMarkdown(text) {
        if (!text) return '';
        if (typeof marked !== 'undefined' && marked.parse) {
            return marked.parse(text, { breaks: true, gfm: true });
        }
        return escapeHtml(text).replace(/\n/g, '<br>');
    }

    function escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    // ============================================================
    // Code Block Copy
    // ============================================================
    function addCodeCopyButtons(container) {
        container.querySelectorAll('pre').forEach(function (pre) {
            if (pre.parentElement?.classList.contains('code-header')) return;

            const code = pre.querySelector('code');
            if (!code) return;

            const lang = (code.className || '').replace('language-', '').trim() || 'کد';

            const header = document.createElement('div');
            header.className = 'code-header';

            const langLabel = document.createElement('span');
            langLabel.textContent = lang;
            header.appendChild(langLabel);

            const copyBtn = document.createElement('button');
            copyBtn.className = 'copy-code-btn';
            copyBtn.innerHTML = '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="9" width="13" height="13" rx="2" ry="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg> کپی';
            copyBtn.addEventListener('click', function (e) {
                e.stopPropagation();
                const text = code.textContent || '';
                navigator.clipboard.writeText(text).then(function () {
                    copyBtn.innerHTML = '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"/></svg> کپی شد';
                    copyBtn.style.color = '#10b981';
                    setTimeout(function () {
                        copyBtn.innerHTML = '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="9" width="13" height="13" rx="2" ry="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg> کپی';
                        copyBtn.style.color = '';
                    }, 2000);
                }).catch(function () {
                    copyBtn.textContent = 'خطا در کپی';
                    setTimeout(function () { copyBtn.textContent = 'کپی'; }, 2000);
                });
            });

            header.appendChild(copyBtn);
            pre.parentNode.insertBefore(header, pre);
        });
    }

    // ============================================================
    // Create Message Elements
    // ============================================================
    function createUserMessage(text) {
        const div = document.createElement('div');
        div.className = 'message user';

        const bubble = document.createElement('div');
        bubble.className = 'message-bubble';
        bubble.textContent = text;
        div.appendChild(bubble);

        return div;
    }

    function createAssistantMessage() {
        const div = document.createElement('div');
        div.className = 'message assistant';

        const bubble = document.createElement('div');
        bubble.className = 'message-bubble';
        div.appendChild(bubble);

        const actions = document.createElement('div');
        actions.className = 'message-actions';
        actions.innerHTML =
            '<button class="message-action-btn" title="کپی" data-action="copy">' +
            '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">' +
            '<rect x="9" y="9" width="13" height="13" rx="2" ry="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg></button>';

        div.appendChild(actions);

        return { container: div, bubble: bubble, actions: actions };
    }

    // ============================================================
    // Show / Hide
    // ============================================================
    function showWelcome() {
        dom.welcomeScreen.hidden = false;
        dom.chatMessages.innerHTML = '';
        state.hasConversation = false;
    }

    function hideWelcome() {
        dom.welcomeScreen.hidden = true;
        state.hasConversation = true;
    }

    function showError(message) {
        dom.errorMessage.textContent = message;
        dom.errorToast.hidden = false;
        setTimeout(function () { dom.errorToast.hidden = true; }, 5000);
    }

    function showTyping() {
        dom.typingIndicator.hidden = false;
        scrollToBottom(true);
    }

    function hideTyping() {
        dom.typingIndicator.hidden = true;
    }

    // ============================================================
    // SSE Chat
    // ============================================================
    function sendMessage(text) {
        if (state.isStreaming) return;
        if (!text || !text.trim()) return;

        hideWelcome();
        dom.errorToast.hidden = true;

        // Add user message
        const userMsg = createUserMessage(text.trim());
        dom.chatMessages.appendChild(userMsg);
        scrollToBottom(true);

        // Create assistant message placeholder
        const assistant = createAssistantMessage();
        dom.chatMessages.appendChild(assistant);
        state.currentAssistantMsg = assistant;
        state.currentAssistantBubble = assistant.bubble;

        // Show typing
        showTyping();

        // Setup abort controller
        state.abortController = new AbortController();
        state.isStreaming = true;

        dom.sendButton.hidden = true;
        dom.stopButton.hidden = false;
        dom.messageInput.disabled = true;

        // Start SSE
        startStream(text, state.abortController.signal);
    }

    function startStream(text, signal) {
        var xhr = new XMLHttpRequest();
        xhr.open('POST', '/api/chat/stream-with-sources', true);
        xhr.setRequestHeader('Content-Type', 'application/json');
        xhr.setRequestHeader('Accept', 'text/event-stream');

        var lastIndex = 0;
        var responseText = '';

        xhr.onreadystatechange = function () {
            if (xhr.readyState === 3 || xhr.readyState === 4) {
                var newData = xhr.responseText.substring(lastIndex);
                lastIndex = xhr.responseText.length;

                var lines = newData.split('\n');
                for (var i = 0; i < lines.length; i++) {
                    var line = lines[i].trim();
                    if (line.startsWith('data: ')) {
                        var payload = line.substring(6);
                        if (payload === '[DONE]') {
                            continue;
                        }
                        try {
                            var parsed = JSON.parse(payload);
                            if (parsed.type === 'text' && parsed.text) {
                                responseText += parsed.text;
                                updateAssistantMessage(responseText);
                            } else if (parsed.text && !parsed.type) {
                                responseText += parsed.text;
                                updateAssistantMessage(responseText);
                            }
                        } catch (e) {
                            // Skip invalid JSON
                        }
                    }
                }

                if (xhr.readyState === 4) {
                    finishStream(responseText);
                }
            }
        };

        xhr.onerror = function () {
            showError('خطا در ارتباط با سرور. لطفاً دوباره تلاش کنید.');
            cancelStream();
        };

        xhr.onabort = function () {
            finishStream(responseText);
        };

        var body = JSON.stringify({
            query: text,
            searchMode: 'hybrid'
        });

        xhr.send(body);

        // Handle abort via AbortController
        signal.addEventListener('abort', function () {
            xhr.abort();
        });
    }

    function updateAssistantMessage(text) {
        hideTyping();
        if (state.currentAssistantBubble) {
            state.currentAssistantBubble.innerHTML = renderMarkdown(text);
            addCodeCopyButtons(state.currentAssistantBubble);
            scrollToBottom(true);
        }
    }

    function finishStream(text) {
        state.isStreaming = false;
        state.abortController = null;

        dom.sendButton.hidden = false;
        dom.stopButton.hidden = true;
        dom.messageInput.disabled = false;
        dom.messageInput.focus();

        hideTyping();

        if (state.currentAssistantBubble && text) {
            state.currentAssistantBubble.innerHTML = renderMarkdown(text);
            addCodeCopyButtons(state.currentAssistantBubble);
        }

        state.currentAssistantMsg = null;
        state.currentAssistantBubble = null;
        scrollToBottom(true);
    }

    function cancelStream() {
        if (state.abortController) {
            state.abortController.abort();
        }
        state.isStreaming = false;
        state.abortController = null;

        dom.sendButton.hidden = false;
        dom.stopButton.hidden = true;
        dom.messageInput.disabled = false;

        hideTyping();
    }

    // ============================================================
    // Copy Message Content
    // ============================================================
    function handleMessageActionClick(e) {
        var btn = e.target.closest('.message-action-btn');
        if (!btn) return;

        var action = btn.getAttribute('data-action');
        if (action === 'copy') {
            var bubble = btn.closest('.message').querySelector('.message-bubble');
            if (!bubble) return;
            var text = bubble.textContent || '';
            navigator.clipboard.writeText(text).catch(function () {
                showError('خطا در کپی متن');
            });
        }
    }

    // ============================================================
    // New Chat
    // ============================================================
    function newChat() {
        if (state.isStreaming) {
            cancelStream();
        }
        showWelcome();
        dom.messageInput.value = '';
        autoResizeTextarea();
        dom.sendButton.disabled = true;
        dom.messageInput.focus();
    }

    // ============================================================
    // Suggestion Chips
    // ============================================================
    function handleSuggestionClick(e) {
        var chip = e.target.closest('.suggestion-chip');
        if (!chip) return;
        var prompt = chip.getAttribute('data-prompt');
        if (prompt) {
            dom.messageInput.value = prompt;
            autoResizeTextarea();
            dom.sendButton.disabled = false;
            dom.messageInput.focus();
            sendMessage(prompt);
        }
    }

    // ============================================================
    // Input Management
    // ============================================================
    function updateSendButton() {
        var text = dom.messageInput.value.trim();
        dom.sendButton.disabled = !text || state.isStreaming;
    }

    function handleKeydown(e) {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            if (!dom.sendButton.disabled) {
                sendMessage(dom.messageInput.value);
                dom.messageInput.value = '';
                autoResizeTextarea();
                dom.sendButton.disabled = true;
            }
        }
    }

    // ============================================================
    // Init
    // ============================================================
    function init() {
        initTheme();

        // Theme toggle
        dom.themeToggle.addEventListener('click', toggleTheme);

        // New chat
        dom.newChatBtn.addEventListener('click', newChat);

        // Message input
        dom.messageInput.addEventListener('input', function () {
            autoResizeTextarea();
            updateSendButton();
        });
        dom.messageInput.addEventListener('keydown', handleKeydown);

        // Send button
        dom.sendButton.addEventListener('click', function () {
            var text = dom.messageInput.value;
            if (text.trim()) {
                sendMessage(text);
                dom.messageInput.value = '';
                autoResizeTextarea();
                dom.sendButton.disabled = true;
            }
        });

        // Stop button
        dom.stopButton.addEventListener('click', cancelStream);

        // Suggestion chips
        dom.welcomeScreen.addEventListener('click', handleSuggestionClick);

        // Message action buttons (copy)
        dom.chatMessages.addEventListener('click', handleMessageActionClick);

        // Initial state
        dom.sendButton.disabled = true;
        dom.messageInput.focus();

        console.log('چت ايسيكو آماده است');
    }

    // Run on DOM ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
