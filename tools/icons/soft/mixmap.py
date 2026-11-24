# The Soft theme's icons: Segoe codepoint -> Google Material Symbols Rounded (Android's icon set, Apache 2.0), via Iconify.
# Same codepoints as ..\mixmap.py, so the app swaps the whole font and every icon follows. (SF Symbols, Apple's, may only
# be used on Apple platforms, so they can't ship here.)
M = 'material-symbols:'   # regular (400) weight: readable at small sizes
MIX = {cp: M + n for cp, n in {
    # chrome, arrows, layout
    0xE70D: 'keyboard-arrow-down-rounded', 0xE70E: 'keyboard-arrow-up-rounded', 0xE76B: 'chevron-left-rounded', 0xE76C: 'chevron-right-rounded',
    0xE710: 'add-rounded', 0xE711: 'close-rounded', 0xE8BB: 'close-rounded', 0xE712: 'more-horiz',
    0xE713: 'settings-outline-rounded', 0xE921: 'remove-rounded', 0xE922: 'crop-square-outline-rounded', 0xE721: 'search-rounded',
    0xE72B: 'arrow-back-rounded', 0xE74B: 'arrow-downward-rounded', 0xE8A7: 'open-in-new-rounded',
    0xE89F: 'right-panel-open-outline-rounded', 0xE8A0: 'splitscreen-right-outline-rounded', 0xE737: 'dashboard-outline-rounded', 0xF57E: 'view-column-outline-rounded',
    0xE76F: 'drag-indicator', 0xE715: 'inbox-outline-rounded',
    # AI concepts and states
    0xE8F1: 'psychology-outline-rounded', 0xEA80: 'lightbulb-outline-rounded', 0xE8BD: 'chat-bubble-outline-rounded', 0xE82D: 'library-books-outline-rounded',
    0xE9D5: 'format-list-numbered-rounded', 0xE8FD: 'checklist-rounded', 0xE783: 'warning-outline-rounded', 0xE946: 'info-outline-rounded',
    0xE7BA: 'notifications-active-outline-rounded', 0xE895: 'sync-rounded', 0xE12A: 'newspaper-rounded', 0xE8D7: 'key-outline-rounded',
    0xE72E: 'lock-outline', 0xE790: 'palette-outline', 0xE9D9: 'monitor-heart-outline-rounded', 0xE773: 'travel-explore-rounded',
    0xE719: 'storefront-outline-rounded', 0xE7B8: 'archive-outline-rounded', 0xE890: 'visibility-outline-rounded', 0xED1A: 'visibility-off-outline-rounded',
    0xE734: 'star-outline-rounded', 0xE735: 'star-rounded', 0xE718: 'keep-outline-rounded', 0xE840: 'keep-rounded', 0xE77A: 'keep-off-outline-rounded',
    0xE7E8: 'power-settings-new-rounded', 0xE71D: 'extension-outline', 0xE9CE: 'build-outline-rounded',
    # files, code, web
    0xE8B7: 'folder-outline-rounded', 0xE838: 'folder-open-outline-rounded', 0xE8DA: 'folder-managed-outline-rounded', 0xE8A5: 'draft-outline-rounded',
    0xE943: 'code-rounded', 0xE756: 'terminal-rounded', 0xE932: 'edit-outline-rounded', 0xE8F4: 'create-new-folder-outline-rounded',
    0xEB9F: 'image-outline-rounded', 0xEA90: 'folder-zip-outline-rounded', 0xF012: 'picture-as-pdf-outline-rounded', 0xEDE1: 'ios-share-rounded',
    0xE723: 'attach-file-rounded', 0xE774: 'language', 0xE74E: 'save-outline-rounded',
    # planner, calendar, modes
    0xE73A: 'task-alt-rounded', 0xEA8F: 'notifications-outline-rounded', 0xE945: 'bolt-outline-rounded',
    0xE706: 'light-mode-outline-rounded', 0xE708: 'dark-mode-outline-rounded', 0xE787: 'calendar-month-outline-rounded',
    0xE823: 'schedule-outline-rounded', 0xE916: 'timer-outline-rounded', 0xE8EE: 'repeat-rounded', 0xE739: 'check-box-outline-blank-rounded',
    # media
    0xE720: 'mic-outline-rounded', 0xE7C8: 'mic-off-outline-rounded', 0xE722: 'photo-camera-outline-rounded', 0xE767: 'volume-up-outline-rounded',
    0xE768: 'play-arrow-rounded', 0xE71A: 'stop-rounded', 0xE724: 'send-outline-rounded',
    # edit actions
    0xE8C8: 'content-copy-outline-rounded', 0xE74D: 'delete-outline-rounded', 0xE70F: 'edit-square-outline-rounded', 0xE72C: 'refresh-rounded',
    0xE7A7: 'undo-rounded', 0xE73E: 'check-rounded',
    # icons for every symbol Ivo uses, one per idea
    0xE70B: 'note-alt-outline-rounded',
    0xE71B: 'link-rounded',
    0xE738: 'do-not-disturb-on-outline-rounded',
    0xE753: 'cloud-outline-rounded',
    0xE769: 'pause-rounded',
    0xE777: 'settings-backup-restore-rounded',
    0xE77B: 'person-outline-rounded',
    0xE77F: 'content-paste-rounded',
    0xE7BC: 'bookmarks-outline-rounded',
    0xE7C1: 'flag-outline-rounded',
    0xE7E7: 'comment-outline-rounded',
    0xE7F4: 'desktop-windows-outline-rounded',
    0xE7F5: 'speaker-outline-rounded',
    0xE7F8: 'laptop-windows-outline-rounded',
    0xE80F: 'home-outline-rounded',
    0xE81C: 'history-rounded',
    0xE892: 'skip-previous-outline-rounded',
    0xE893: 'skip-next-outline-rounded',
    0xE896: 'download-rounded',
    0xE898: 'upload-rounded',
    0xE8A1: 'preview-outline-rounded',
    0xE8AB: 'swap-horiz-rounded',
    0xE8B1: 'shuffle-rounded',
    0xE8C7: 'account-balance-wallet-outline-rounded',
    0xE8D6: 'music-note-rounded',
    0xE8E1: 'thumb-up-outline-rounded',
    0xE8EA: 'open-in-full-rounded',
    0xE8ED: 'repeat-one-rounded',
    0xE90B: 'queue-music-rounded',
    0xE90F: 'handyman-outline-rounded',
    0xE923: 'close-fullscreen-rounded',
    0xE97A: 'reply-rounded',
    0xE9F9: 'description-outline-rounded',
    0xEB51: 'favorite-outline-rounded',
    0xEB52: 'favorite-rounded',
    0xEC05: 'podcasts-rounded',
    0xE99A: 'smart-toy-outline-rounded',
    0xEC27: 'account-tree-outline-rounded',
    0xE8F2: 'format-quote-outline-rounded',
    0xEA18: 'shield-outline-rounded',
    0xEA3A: 'auto-awesome-outline-rounded',
    0xED1E: 'lyrics-outline-rounded',   # lyrics
    0xED21: 'target',   # focus
    0xED22: 'sticky-note-2-outline-rounded',   # notes
    0xED23: 'webhook-rounded',   # webhooks
    0xED24: 'potted-plant-outline-rounded',   # habits
    0xED25: 'document-scanner-outline-rounded',   # text from the screen (OCR)
    0xED26: 'cyclone-rounded',   # portal
}.items()}
