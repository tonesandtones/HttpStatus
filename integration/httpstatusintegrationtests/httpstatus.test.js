const axios = require('axios').default;
const config = require('config');
const https = require('https');
const each = require('jest-each').default;

const ConfigName_TestBaseUrl = "IntegrationTestBaseUrl"
const ConfigName_IgnoreSelfSigned = 'IgnoreSelfSigned';

let baseUrl = config.get(ConfigName_TestBaseUrl);
if (!baseUrl.endsWith("/")){
    baseUrl += "/";
}
console.log('NODE_ENV: ' + config.util.getEnv('NODE_ENV'));

const instance = axios.create({
    httpsAgent: new https.Agent({
        rejectUnauthorized: !config.get(ConfigName_IgnoreSelfSigned),
        timeout: 200
    })
});

describe('Static hello world', () => {
    test('GET /', async () => {
        // let result = null;
        let result = await instance.get(baseUrl, {validateStatus: () => true});
        expect(result.status).toBe(200);
        expect(result.headers["content-type"]).toContain("text/plain");
        expect(result.data).toBe("Hello World!");
    })
})

//200 to 999
//The 1xx codes mean something to too many things, so don't bother testing them.
const statusCodes = Array.from(new Array(800), (x, i) => [i + 200]);

//A handful of specific codes cannot have content. Don't test these for content
const noContentCodes = [204, 205, 304]

describe('Status codes', () => {
    each(statusCodes)
        .test('Status code: %p', async (expectedStatusCode) => {
            let url = new URL(expectedStatusCode, baseUrl);
            let result = await instance.get(url.toString(), {validateStatus: (ignore) => true});
            expect(result.status).toBe(expectedStatusCode);

            if (noContentCodes.includes(expectedStatusCode)) {
                expect(result.data).toBe("");
            } else {
                expect(result.data).toBe(expectedStatusCode);
            }
        })
})
