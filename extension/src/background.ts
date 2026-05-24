import { logger } from './shared/logger';

const log = logger('background');

chrome.runtime.onInstalled.addListener((details) => {
  log.info('extension installed', { reason: details.reason });
});

log.info('service worker started');
