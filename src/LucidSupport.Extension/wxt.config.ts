import { defineConfig } from 'wxt';

export default defineConfig({
  manifest: {
    name: 'LucidSupport Trainer',
    description: 'Extract form fields from any page to train LucidSupport',
    permissions: ['sidePanel', 'activeTab', 'storage'],
    action: {
      default_title: 'LucidSupport Trainer',
    },
    side_panel: {
      default_path: 'sidepanel/index.html',
    },
  },
});
