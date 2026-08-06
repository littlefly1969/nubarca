/// <reference types="expo/types" />

// Expo inlines EXPO_PUBLIC_* environment variables at build time (Metro/Babel).
// The TV app reads exactly one (see app.config.js + resolveBaseUrl in App.tsx).
// Declare a minimal `process.env` shape here so the strict TS build resolves it
// without pulling the full Node global type surface into the React Native type
// space (react-native does not declare `process`, and neither does expo/types).
declare const process: {
  env: {
    EXPO_PUBLIC_NUBARCA_API_BASE_URL?: string;
  } & Record<string, string | undefined>;
};
