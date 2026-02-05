import { extractPage } from '@/lib/extractor';

export default defineContentScript({
  matches: ['<all_urls>'],
  main() {
    browser.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
      if (msg?.type === 'EXTRACT_PAGE') {
        const result = extractPage();
        sendResponse(result);
      }
      // Return true to indicate async sendResponse
      return true;
    });
  },
});
