module.exports = function (api) {
  api.cache(true);
  return {
    presets: ['babel-preset-expo'],
    // Reanimated 4 compiles its worklets through react-native-worklets. The
    // plugin has to stay LAST in the list, and without it every gesture-driven
    // animated style silently fails to run on the UI thread.
    plugins: ['react-native-worklets/plugin'],
  };
};
