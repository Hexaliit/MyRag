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
        const assistantMsg = createAssistantMessage();
        dom.chatMessages.appendChild(assistantMsg.container);
        state.currentAssistantMsg = assistantMsg;
        state.currentAssistantBubble = assistantMsg.bubble;

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
    // Admin Features - امکانات ادمین
    // ============================================================
    function renderChatHistory() {
        var historyList = document.getElementById('chatHistoryList');
        if (!historyList) return;

        var history = JSON.parse(localStorage.getItem('chatHistory') || '[]');

        if (history.length === 0) {
            historyList.innerHTML = '<div class="text-center text-xs opacity-50 py-8">هیچ مکالمه‌ای وجود ندارد</div>';
            return;
        }

        historyList.innerHTML = history.map(function(chat, index) {
            return '<div class="chat-history-item ' + (chat.isActive ? 'active' : '') + '" data-index="' + index + '">' +
                '<div class="chat-history-content">' +
                '<div class="chat-history-title">' + chat.title + '</div>' +
                '<div class="chat-history-date">' + chat.date + '</div>' +
                '</div>' +
                '<button class="chat-history-delete" data-action="delete" data-index="' + index + '" title="حذف">' +
                '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">' +
                '<path d="M3 6h18M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/>' +
                '</svg>' +
                '</button>' +
                '</div>';
        }).join('');

        historyList.querySelectorAll('.chat-history-item').forEach(function(item) {
            item.addEventListener('click', function(e) {
                if (e.target.closest('.chat-history-delete')) return;
                var index = parseInt(this.getAttribute('data-index'));
                loadChatHistory(index);
            });
        });

        historyList.querySelectorAll('.chat-history-delete').forEach(function(btn) {
            btn.addEventListener('click', function(e) {
                e.stopPropagation();
                var index = parseInt(this.getAttribute('data-index'));
                deleteChatHistory(index);
            });
        });
    }

    function loadChatHistory(index) {
        var history = JSON.parse(localStorage.getItem('chatHistory') || '[]');
        if (index >= 0 && index < history.length) {
            showWelcome();
            history.forEach(function(chat, i) {
                chat.isActive = (i === index);
            });
            localStorage.setItem('chatHistory', JSON.stringify(history));
            renderChatHistory();

            if (history[index].messages && history[index].messages.length > 0) {
                history[index].messages.forEach(function(msg) {
                    if (msg.role === 'user') {
                        var userMsg = createUserMessage(msg.content);
                        dom.chatMessages.appendChild(userMsg);
                    }
                });
                hideWelcome();
            }
        }
    }

    function deleteChatHistory(index) {
        var history = JSON.parse(localStorage.getItem('chatHistory') || '[]');
        history.splice(index, 1);
        localStorage.setItem('chatHistory', JSON.stringify(history));
        renderChatHistory();
    }

    function saveChatToHistory(title, messages) {
        var history = JSON.parse(localStorage.getItem('chatHistory') || '[]');
        var existingIndex = history.findIndex(function(chat) {
            return chat.isActive;
        });

        if (existingIndex >= 0) {
            history[existingIndex].messages = messages;
            history[existingIndex].title = title || history[existingIndex].title;
        } else {
            history.push({
                title: title || 'مکالمه جدید',
                date: new Date().toLocaleDateString('fa-IR'),
                messages: messages,
                isActive: true
            });
        }

        localStorage.setItem('chatHistory', JSON.stringify(history));
        renderChatHistory();
    }

    function showChatStats() {
        var history = JSON.parse(localStorage.getItem('chatHistory') || '[]');
        var totalChats = history.length;
        var totalMessages = history.reduce(function(sum, chat) {
            return sum + (chat.messages ? chat.messages.length : 0);
        }, 0);
        var favoriteChats = history.filter(function(chat) {
            return chat.isFavorite;
        }).length;

        var overlay = document.createElement('div');
        overlay.className = 'panel-overlay';
        overlay.addEventListener('click', function() {
            document.body.removeChild(overlay);
            document.body.removeChild(panel);
        });

        var panel = document.createElement('div');
        panel.className = 'chat-stats-panel';
        panel.innerHTML = '<h3>آمار چت</h3>' +
            '<div class="chat-stats-grid">' +
            '<div class="chat-stat-item"><div class="chat-stat-value">' + totalChats + '</div><div class="chat-stat-label">تعداد مکالمات</div></div>' +
            '<div class="chat-stat-item"><div class="chat-stat-value">' + totalMessages + '</div><div class="chat-stat-label">تعداد پیام‌ها</div></div>' +
            '<div class="chat-stat-item"><div class="chat-stat-value">' + favoriteChats + '</div><div class="chat-stat-label">مکالمات مورد علاقه</div></div>' +
            '<div class="chat-stat-item"><div class="chat-stat-value">' + new Date().toLocaleDateString('fa-IR') + '</div><div class="chat-stat-label">تاریخ امروز</div></div>' +
            '</div>' +
            '<div style="text-align: center; margin-top: 16px;"><button class="btn-primary" id="closeStatsPanel">بستن</button></div>';

        document.body.appendChild(overlay);
        document.body.appendChild(panel);

        panel.querySelector('#closeStatsPanel').addEventListener('click', function() {
            document.body.removeChild(overlay);
            document.body.removeChild(panel);
        });
    }

    function showChatSettings() {
        var overlay = document.createElement('div');
        overlay.className = 'panel-overlay';
        overlay.addEventListener('click', function() {
            document.body.removeChild(overlay);
            document.body.removeChild(panel);
        });

        var panel = document.createElement('div');
        panel.className = 'chat-settings-panel';
        panel.innerHTML = '<h3>تنظیمات چت</h3>' +
            '<div class="chat-settings-group">' +
            '<label class="chat-settings-label">نام نمایشی</label>' +
            '<input type="text" class="chat-settings-input" id="displayNameInput" value="' + (localStorage.getItem('chatDisplayName') || 'کاربر') + '" />' +
            '</div>' +
            '<div class="chat-settings-group">' +
            '<label class="chat-settings-label">موضوع پیش‌فرض</label>' +
            '<input type="text" class="chat-settings-input" id="defaultTopicInput" value="' + (localStorage.getItem('chatDefaultTopic') || 'اسناد سازمانی') + '" />' +
            '</div>' +
            '<div style="display: flex; gap: 8px; justify-content: center; margin-top: 16px;">' +
            '<button class="btn-primary" id="saveSettings">ذخیره</button>' +
            '<button class="btn-secondary" id="closeSettings">لغو</button>' +
            '</div>';

        document.body.appendChild(overlay);
        document.body.appendChild(panel);

        panel.querySelector('#saveSettings').addEventListener('click', function() {
            localStorage.setItem('chatDisplayName', panel.querySelector('#displayNameInput').value);
            localStorage.setItem('chatDefaultTopic', panel.querySelector('#defaultTopicInput').value);
            document.body.removeChild(overlay);
            document.body.removeChild(panel);
        });

        panel.querySelector('#closeSettings').addEventListener('click', function() {
            document.body.removeChild(overlay);
            document.body.removeChild(panel);
        });
    }

    function showAllChats() {
        var history = JSON.parse(localStorage.getItem('chatHistory') || '[]');

        var overlay = document.createElement('div');
        overlay.className = 'panel-overlay';
        overlay.addEventListener('click', function() {
            document.body.removeChild(overlay);
            document.body.removeChild(panel);
        });

        var panel = document.createElement('div');
        panel.className = 'all-chats-panel';

        if (history.length === 0) {
            panel.innerHTML = '<h3>تمام مکالمات</h3><div class="text-center text-sm opacity-50 py-8">هیچ مکالمه‌ای وجود ندارد</div>';
        } else {
            var listHtml = history.map(function(chat, index) {
                return '<div class="all-chats-item" data-index="' + index + '">' +
                    '<div class="all-chats-item-content">' +
                    '<div class="all-chats-item-title">' + chat.title + '</div>' +
                    '<div class="all-chats-item-date">' + chat.date + '</div>' +
                    '</div>' +
                    '<div class="all-chats-item-actions">' +
                    '<button class="btn-secondary btn-sm" data-action="load" data-index="' + index + '">بارگذاری</button>' +
                    '<button class="btn-danger btn-sm" data-action="delete" data-index="' + index + '">حذف</button>' +
                    '</div>' +
                    '</div>';
            }).join('');

            panel.innerHTML = '<h3>تمام مکالمات</h3><div class="all-chats-list">' + listHtml + '</div>';
        }

        panel.innerHTML += '<div style="text-align: center; margin-top: 16px;"><button class="btn-primary" id="closeAllChats">بستن</button></div>';

        document.body.appendChild(overlay);
        document.body.appendChild(panel);

        panel.querySelector('#closeAllChats').addEventListener('click', function() {
            document.body.removeChild(overlay);
            document.body.removeChild(panel);
        });

        panel.querySelectorAll('[data-action="load"]').forEach(function(btn) {
            btn.addEventListener('click', function() {
                var index = parseInt(this.getAttribute('data-index'));
                loadChatHistory(index);
                document.body.removeChild(overlay);
                document.body.removeChild(panel);
            });
        });

        panel.querySelectorAll('[data-action="delete"]').forEach(function(btn) {
            btn.addEventListener('click', function() {
                var index = parseInt(this.getAttribute('data-index'));
                deleteChatHistory(index);
                document.body.removeChild(overlay);
                document.body.removeChild(panel);
                showAllChats();
            });
        });
    }

    function exportChatHistory() {
        var history = JSON.parse(localStorage.getItem('chatHistory') || '[]');
        var dataStr = JSON.stringify(history, null, 2);
        var dataUri = 'data:application/json;charset=utf-8,' + encodeURIComponent(dataStr);
        var exportFileDefaultName = 'lucidrag-chat-history-' + new Date().toISOString().slice(0, 10) + '.json';
        var linkElement = document.createElement('a');
        linkElement.setAttribute('href', dataUri);
        linkElement.setAttribute('download', exportFileDefaultName);
        linkElement.click();
    }

    function toggleFavoriteChat() {
        var history = JSON.parse(localStorage.getItem('chatHistory') || '[]');
        var activeIndex = history.findIndex(function(chat) {
            return chat.isActive;
        });

        if (activeIndex >= 0) {
            history[activeIndex].isFavorite = !history[activeIndex].isFavorite;
            localStorage.setItem('chatHistory', JSON.stringify(history));
            renderChatHistory();
        }
    }

    function clearAllChatData() {
        if (confirm('آیا از پاک کردن تمام داده‌های چت اطمینان دارید؟ این عمل غیرقابل بازگشت است.')) {
            localStorage.removeItem('chatHistory');
            localStorage.removeItem('chatDisplayName');
            localStorage.removeItem('chatDefaultTopic');
            renderChatHistory();
            showWelcome();
            dom.messageInput.value = '';
            autoResizeTextarea();
            dom.sendButton.disabled = true;
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

        // Sidebar admin buttons
        var toggleSidebar = document.getElementById('toggleSidebar');
        var themeToggleSidebar = document.getElementById('themeToggleSidebar');
        var themeToggleMain = document.getElementById('themeToggleMain');
        var newChatBtnSidebar = document.getElementById('newChatBtnSidebar');
        var newChatBtnMain = document.getElementById('newChatBtnMain');
        var clearChatHistory = document.getElementById('clearChatHistory');
        var showAllChatsBtn = document.getElementById('showAllChats');
        var exportChatsBtn = document.getElementById('exportChats');
        var chatSettingsBtn = document.getElementById('chatSettings');
        var chatStatsBtn = document.getElementById('chatStats');
        var favoriteChatsBtn = document.getElementById('favoriteChats');
        var exportChatHistoryBtn = document.getElementById('exportChatHistory');
        var clearAllDataBtn = document.getElementById('clearAllData');
        var chatSidebar = document.getElementById('chatSidebar');

        if (toggleSidebar && chatSidebar) {
            toggleSidebar.addEventListener('click', function() {
                chatSidebar.classList.toggle('open');
            });
        }

        if (themeToggleSidebar) {
            themeToggleSidebar.addEventListener('click', toggleTheme);
        }

        if (themeToggleMain) {
            themeToggleMain.addEventListener('click', toggleTheme);
        }

        if (newChatBtnSidebar) {
            newChatBtnSidebar.addEventListener('click', newChat);
        }

        if (newChatBtnMain) {
            newChatBtnMain.addEventListener('click', newChat);
        }

        if (clearChatHistory) {
            clearChatHistory.addEventListener('click', function() {
                if (confirm('آیا از پاک کردن تاریخچه مکالمات اطمینان دارید؟')) {
                    localStorage.removeItem('chatHistory');
                    renderChatHistory();
                    showWelcome();
                }
            });
        }

        if (showAllChatsBtn) {
            showAllChatsBtn.addEventListener('click', showAllChats);
        }

        if (exportChatsBtn) {
            exportChatsBtn.addEventListener('click', exportChatHistory);
        }

        if (chatSettingsBtn) {
            chatSettingsBtn.addEventListener('click', showChatSettings);
        }

        if (chatStatsBtn) {
            chatStatsBtn.addEventListener('click', showChatStats);
        }

        if (favoriteChatsBtn) {
            favoriteChatsBtn.addEventListener('click', toggleFavoriteChat);
        }

        if (exportChatHistoryBtn) {
            exportChatHistoryBtn.addEventListener('click', exportChatHistory);
        }

        if (clearAllDataBtn) {
            clearAllDataBtn.addEventListener('click', clearAllChatData);
        }

        // Initial state
        dom.sendButton.disabled = true;
        dom.messageInput.focus();
        renderChatHistory();

        console.log('چت لوسیدراگ آماده است');
    }

    // Run on DOM ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
