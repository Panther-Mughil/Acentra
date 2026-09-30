// Global helper scripts for ORGSHIELD 360

window.orgshield = {
    // Download text as file (for CSV export)
    downloadFile: function (filename, content, contentType) {
        const blob = new Blob([content], { type: contentType || 'text/csv;charset=utf-8;' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
    },

    // Register global key listener for Ctrl+K
    initKeybindings: function (dotNetHelper) {
        window.addEventListener('keydown', function (e) {
            if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
                e.preventDefault();
                dotNetHelper.invokeMethodAsync('OpenCommandPalette');
            }
            if (e.key === 'Escape') {
                dotNetHelper.invokeMethodAsync('CloseModals');
            }
        });
    }
};
